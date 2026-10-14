using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Memory;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;
using System.Globalization;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Entities;

namespace OpenPrefirePrac;

public partial class OpenPrefirePrac : BasePlugin
{
    public override string ModuleName => "Open Prefire Prac";
    public override string ModuleVersion => "0.1.49";
    public override string ModuleAuthor => "Lengran";
    public override string ModuleDescription => "A plugin for practicing prefire in CS2. https://github.com/lengran/OpenPrefirePrac";

    private readonly Dictionary<int, PlayerStatus> _playerStatuses = new();         // Key: slot of player
    
    private readonly Dictionary<int, int> _ownerOfBots = new();       // Map: slot of bot -> owner.
    
    private readonly Dictionary<string, int> _practiceNameToId = new();
    
    private readonly Dictionary<int, int> _pracConflictCount = new();           // Num of on-going practices that might interfere
    
    private string _mapName = "";
    
    private int _playerCount;
    
    private readonly List<PrefirePractice> _practices = new();
    
    private readonly List<string> _availableMaps = new();

    private readonly ServerStatus _serverStatus = new();

    private Translator ?_translator;

    private readonly Dictionary<int, int> _botRequests = new();         // make this thread-safe if necessary. Map: Slot of player -> number of bots needed

    private DefaultConfig ?_defaultPlayerSettings;

    private CommandDefinition ?_command;

    private CounterStrikeSharp.API.Modules.Timers.Timer ?_timerBroadcastProgress;

    // Scratch state for the aim-lock tick loop, reused across ticks so the hot path allocates
    // nothing. QAngle exposes X/Y/Z as `ref float` into its own native buffer, so mutating one
    // instance in place is equivalent to handing Teleport a fresh one, minus the per-bot
    // AllocHGlobal every tick.
    private readonly QAngle _aimLockAngle = new(0, 0, 0);

    private readonly List<int> _aimLockSlots = new();

    // Degrees a frozen bot's body may be off the direction to its player before the aim lock turns it.
    private const double AimLockYawTolerance = 5.0;

    // Practices are played against bots, so they cannot work on a server started with -nobots:
    // bot_add does not give working bots there and players end up shooting at invisible targets.
    private bool _botsDisabled;

    public override void Load(bool hotReload)
    {
        _playerCount = 0;

        try
        {
            _botsDisabled = CommandLine.HasParam("-nobots");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[OpenPrefirePrac] Could not read the server command line: {e.Message}");
            _botsDisabled = false;
        }

        if (_botsDisabled)
        {
            Console.WriteLine("[OpenPrefirePrac] The server was started with -nobots. Prefire practices need bots and are disabled; remove -nobots from the start parameters to use them.");
        }

        _translator = new Translator(Localizer, ModuleDirectory, CultureInfo.CurrentCulture.Name);
        
        RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServerHandler);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnectHandler);
        RegisterListener<Listeners.OnMapStart>(OnMapStartHandler);
        RegisterListener<Listeners.OnTick>(OnTickHandler);

        _defaultPlayerSettings = new DefaultConfig(ModuleDirectory);
        _defaultPlayerSettings.LoadDefaultSettings();

        if (hotReload)
        {
            // Clear status registers
            _ownerOfBots.Clear();
            _practiceNameToId.Clear();
            _pracConflictCount.Clear();
            _practices.Clear();
            _availableMaps.Clear();
            _mapName = "";
            _playerCount = 0;
            _playerStatuses.Clear();
            _botRequests.Clear();
            
            // Clear saved convars
            _serverStatus.BoolConvars.Clear();
            _serverStatus.IntConvars.Clear();
            _serverStatus.FloatConvars.Clear();
            _serverStatus.StringConvars.Clear();

            // // Setup map (20250109: Moved outside of the hotReload block)
            // OnMapStartHandler(Server.MapName);
        }

        // Setup players already on the server. Runs regardless of hotReload so a
        // cold mid-map load (css_plugins load) registers present players too,
        // instead of needing a manual reload. Handler is idempotent.
        foreach (var tempPlayer in Utilities.GetPlayers())
        {
            if (tempPlayer == null || tempPlayer.IsBot || tempPlayer.IsHLTV)
            {
                continue;
            }

            OnClientPutInServerHandler(tempPlayer.Slot);
        }

        // Setup map and practice routes
        // Note 20250109: 
        //   This line is moved from inside the hotReload block to support 
        //   mannually-unload-then-load way of loading the plugin. It seems
        //   work okay except throwing a "Failed to load practices on map" 
        //   during server's cold start phase so I guess I'll just leave it
        //   here. If the cold start is found broken in any future issues, 
        //   find another way to do this. Otherwise delete these comments 
        //   later.
        OnMapStartHandler(Server.MapName);

        RegisterCommand();

        // Loaded by !prefireon (fshost CustomCommands) while players are on the server: tell them how to
        // pick a route, since !prefireon only loads the plugin and !prefire opens its menu.
        if (!hotReload)
        {
            AddTimer(3f, () =>
            {
                foreach (var p in Utilities.GetPlayers())
                {
                    if (p != null && p.IsValid && !p.IsBot && !p.IsHLTV)
                        p.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White}{_translator!.Translate(p, "mainmenu.loaded")}");
                }
            });
        }

        if (_timerBroadcastProgress == null)
        {
            _timerBroadcastProgress = AddTimer(3f, () => PrintProgress(), TimerFlags.REPEAT);
        }

        Console.WriteLine("[OpenPrefirePrac] Plugin has been loaded.");
    }

    public override void Unload(bool hotReload)
    {
        UnregisterCommand();

        RemoveListener<Listeners.OnClientPutInServer>(OnClientPutInServerHandler);
        RemoveListener<Listeners.OnClientDisconnect>(OnClientDisconnectHandler);
        RemoveListener<Listeners.OnMapStart>(OnMapStartHandler);
        RemoveListener<Listeners.OnTick>(OnTickHandler);

        if (hotReload)
        {
            // Kick bots spawned for any in-progress practice before the old
            // assembly goes away. Done here (not in Load) because this instance
            // still has the owner bookkeeping KickBot needs to clean up.
            foreach (var tempBot in Utilities.GetPlayers())
            {
                if (tempBot != null && tempBot.IsValid && tempBot.IsBot && !tempBot.IsHLTV)
                {
                    KickBot(tempBot.Slot);
                }
            }

            // Clear status registers
            _ownerOfBots.Clear();
            _practiceNameToId.Clear();
            _pracConflictCount.Clear();
            _practices.Clear();
            _availableMaps.Clear();
            _mapName = "";
            _playerCount = 0;
            _playerStatuses.Clear();
            _botRequests.Clear();
            
            // Clear saved convars
            _serverStatus.BoolConvars.Clear();
            _serverStatus.IntConvars.Clear();
            _serverStatus.FloatConvars.Clear();
            _serverStatus.StringConvars.Clear();
        }
        else
        {
            // Plain unload (css_plugins unload, e.g. fshost's CustomCommands switching mode):
            // remove the practice bots this plugin spawned and put back the convars it changed,
            // so the next mode does not inherit them.
            foreach (var botSlot in _ownerOfBots.Keys.ToList())
            {
                KickBot(botSlot);
            }

            RestoreConvars();
        }

        if (_timerBroadcastProgress != null)
        {
            _timerBroadcastProgress.Kill();
            _timerBroadcastProgress = null;
        }

        Console.WriteLine("[OpenPrefirePrac] Plugin has been unloaded.");
    }

    // TODO: Figure out if we can use the GameEventHandler attribute here instead
    // [GameEventHandler]
    public void OnClientPutInServerHandler(int slot)
    {
        // var player = new CCSPlayerController(NativeAPI.GetEntityFromIndex(slot + 1));
        var player = Utilities.GetPlayerFromSlot(slot);

        if (player == null || !player.IsValid || player.IsHLTV)
        {
            return;
        }

        if (player.IsBot)
        {
            // For bots: If someone is practicing and it's an unmanaged bot, add or kick the bot
            if (_playerCount > 0 && !_ownerOfBots.ContainsKey(slot))
            {
                if (_botRequests.Count > 0)
                {
                    // Update requests (move this to the begining of this block)
                    var tmpPlayerNumBots = _botRequests.FirstOrDefault();
                    if (tmpPlayerNumBots.Value == 1)
                    {
                        _botRequests.Remove(tmpPlayerNumBots.Key);
                    }
                    else
                    {
                        _botRequests[tmpPlayerNumBots.Key]--;
                    }

                    // Put this bot under management
                    _playerStatuses[tmpPlayerNumBots.Key].Bots.Add(slot);
                    _ownerOfBots.Add(slot, tmpPlayerNumBots.Key);
                    Console.WriteLine($"[OpenPrefirePrac] Bot {player.PlayerName}, slot: {slot} has been spawned.");
                }
                else
                {
                    // Already have enough bots, kick this bot and make sure the quota doesn't add it back.
                    KickBot(slot);
                    SyncBotQuota();
                }
            }
        }
        else
        {
            // For players:
            // Guard against double-registration (e.g. the Load() setup loop
            // running for a player who is then re-announced by the engine).
            if (!_playerStatuses.ContainsKey(slot))
            {
                _playerStatuses.Add(slot, new PlayerStatus(_defaultPlayerSettings!));
            }

            // Record player language
            _translator!.RecordPlayerCulture(player);
        }
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player == null)
        {
            // Console.WriteLine("[OpenPrefirePrac] OnPlayerDisconnect doesn't work now. Player = null.");
            return HookResult.Continue;
        }

        if (!_playerStatuses.ContainsKey(player.Slot))
            return HookResult.Continue;

        if (_playerStatuses[player.Slot].PracticeIndex != -1)
        {
            ExitPrefireMode(player.Slot);
        }

        // Release resources(practices, targets, bots...)
        _playerStatuses.Remove(player.Slot);
        UpdateGuidingLineTransmit();
        if (_botRequests.ContainsKey(player.Slot))
        {
            _botRequests.Remove(player.Slot);
        }

        return HookResult.Continue;
    }

    // Engine-level disconnect. Fires reliably (incl. timeout/crash/kick where the
    // EventPlayerDisconnect game event has a null Userid). Idempotent with
    // OnPlayerDisconnect via the ContainsKey guard.
    private void OnClientDisconnectHandler(int slot)
    {
        if (!_playerStatuses.ContainsKey(slot))
            return;

        if (_playerStatuses[slot].PracticeIndex != -1)
        {
            ExitPrefireMode(slot);
        }

        _playerStatuses.Remove(slot);

        UpdateGuidingLineTransmit();
        _botRequests.Remove(slot);   // Remove() on absent key is a safe no-op
    }

    public void OnMapStartHandler(string map)
    {
        _mapName = map;

        // Prefire servers don't need a freeze time, even before a route is started. A map (re)load
        // brings the game mode's freeze time back after !prefireon set it to 0, so clear it again
        // once the map's own cfgs have run. Practices set their full convars when they start.
        AddTimer(2f, () => Server.ExecuteCommand("mp_freezetime 0; mp_team_intro_time 0"), CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);

        // load practices available in current map, from corresponding map directory.
        _availableMaps.Clear();
        var mapDirectories = new List<string>(Directory.EnumerateDirectories(ModuleDirectory + "/maps"));
        var found = false;
        for (var i = 0; i < mapDirectories.Count; i++)
        {
            var mapPath = mapDirectories[i].Substring(mapDirectories[i].LastIndexOf(Path.DirectorySeparatorChar) + 1);
            // Console.WriteLine($"[OpenPrefirePrac] Map folder for map {mapPath} found.");
            _availableMaps.Add(mapPath);

            if (mapPath.Equals(_mapName))
            {
                found = true;
                Console.WriteLine("[OpenPrefirePrac] Map folder for current map found.");
            }
        }

        if (found)
        {
            LoadPractice();
        }
        else
        {
            Console.WriteLine("[OpenPrefirePrac] Failed to load practices on map " + _mapName);
        }
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var playerOrBot = @event.Userid;
        
        if (playerOrBot == null || !playerOrBot.IsValid|| playerOrBot.IsHLTV)
        {
            // Console.WriteLine($"[OpenPrefirePrac] DEBUG: player is null or hltv.");
            return HookResult.Continue;
        }
        
        if (playerOrBot.IsBot)
        {
            // Console.WriteLine($"[OpenPrefirePrac] DEBUG: A bot {playerOrBot.PlayerName} just spawned.");
            if (_ownerOfBots.ContainsKey(playerOrBot.Slot))
            {
                // Console.WriteLine($"[OpenPrefirePrac] DEBUG: {playerOrBot.PlayerName} is a managed bot.");
                // For managed bots
                int ownerSlot = _ownerOfBots[playerOrBot.Slot];
                var targetNo = _playerStatuses[ownerSlot].Progress;
                var practiceIndex = _playerStatuses[ownerSlot].PracticeIndex;

                if (targetNo < _playerStatuses[ownerSlot].EnabledTargets.Count)
                {
                    // If there are more targets to place, move bot to next place
                    _playerStatuses[ownerSlot].Progress++;
                    // Console.WriteLine($"[OpenPrefirePrac] DEBUG: Update progress to {_playerStatuses[owner].Progress}.");

                    // Resolve target now; dictionaries may be gone by the time the timers fire
                    // (owner can exit practice in the 0.55s gap -> KeyNotFoundException).
                    var target = _practices[practiceIndex].Targets[_playerStatuses[ownerSlot].EnabledTargets[targetNo]];
                    var botSlot = playerOrBot.Slot;

                    // Move first, freeze after: the bot used to be frozen at 0.5s and moved at 0.55s, so it
                    // was frozen mid-run (left out of spawn) and kept its running legs at the target.
                    // Teleport it with zero velocity right after spawning, then freeze it once it stands
                    // still, and place it once more so it is exactly on the spot.
                    AddTimer(0.1f, () =>
                    {
                        // Bail if owner stopped practicing or bot got kicked meanwhile.
                        if (!_ownerOfBots.ContainsKey(botSlot) || !_playerStatuses.ContainsKey(ownerSlot))
                            return;

                        MovePlayer(playerOrBot, target.IsCrouching, target.Position, target.Rotation);
                    });

                    AddTimer(0.4f, () =>
                    {
                        if (!_ownerOfBots.ContainsKey(botSlot) || !_playerStatuses.ContainsKey(ownerSlot))
                            return;

                        FreezeBot(playerOrBot);
                        MovePlayer(playerOrBot, target.IsCrouching, target.Position, target.Rotation);
                    });
                    

                    // Give bot weapons
                    SetMoney(playerOrBot, 0);
                    playerOrBot.RemoveWeapons();
                    switch (_playerStatuses[ownerSlot].BotWeapon)
                    {
                        case 1:
                            playerOrBot.GiveNamedItem("weapon_ump45");
                            break;
                        case 2:
                            playerOrBot.GiveNamedItem("weapon_ak47");
                            break;
                        case 3:
                            playerOrBot.GiveNamedItem("weapon_ssg08");
                            break;
                        case 4:
                            playerOrBot.GiveNamedItem("weapon_awp");
                            break;
                        default:
                            // 0: random primary, picked in-code (native bot buy unreliable)
                            string[] randomWeapons = { "weapon_ump45", "weapon_ak47", "weapon_ssg08", "weapon_awp" };
                            playerOrBot.GiveNamedItem(randomWeapons[Random.Shared.Next(randomWeapons.Length)]);
                            break;
                    }

                    // Try to increase bot difficulty
                    // playerOrBot.beha
                }
                else
                {
                    // This code block is to patch the issue of extra bots.
                    // Explain:
                    //     Bot B is died while Bot A is still spawning, so progress 
                    //     is not updated in time. This could cause Bot B not being
                    //     kicked. So kick them here.
                    _ownerOfBots.Remove(playerOrBot.Slot);
                    _playerStatuses[ownerSlot].Bots.Remove(playerOrBot.Slot);
                    KickBot(playerOrBot.Slot);

                    if (_playerStatuses[ownerSlot].Bots.Count == 0)
                    {
                        // Practice finished.
                        var owner = Utilities.GetPlayerFromSlot(ownerSlot);

                        if (owner != null && owner.IsValid && !owner.IsBot && !owner.IsHLTV)
                        {
                            owner.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White}{_translator!.Translate(owner, "practice.finish")}");
                            ExitPrefireMode(ownerSlot);
                        }
                    }
                }
            }
            // else if (_playerCount > 0)
            // {
            //     // For unmanaged bots, kick them.
            //     Console.WriteLine($"[OpenPrefirePrac] Find an unmanaged bot ({playerOrBot.PlayerName}) spawning, kick it.");
            //     KickBot(playerOrBot.Slot);
            // }
        }
        else
        {
            // Console.WriteLine($"[OpenPrefirePrac] DEBUG: A player {playerOrBot.PlayerName} just spawned.");
            // For players: Set them up if they are practicing.
            if (!_playerStatuses.ContainsKey(playerOrBot.Slot))
                return HookResult.Continue;

            if (_playerStatuses[playerOrBot.Slot].PracticeIndex < 0)
                return HookResult.Continue;

            // Console.WriteLine($"[OpenPrefirePrac] DEBUG: Setup player {playerOrBot.PlayerName}.");
            SetupPrefireMode(playerOrBot);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var playerOrBot = @event.Userid;
        
        if (playerOrBot == null || !playerOrBot.IsValid || playerOrBot.IsHLTV)
        {
            return HookResult.Continue;
        }
        
        if (playerOrBot.IsBot) 
        {
            if (_ownerOfBots.ContainsKey(playerOrBot.Slot))
            {
                // For managed bots
                int ownerSlot = _ownerOfBots[playerOrBot.Slot];
                var targetNo = _playerStatuses[ownerSlot].Progress;
                var practiceIndex = _playerStatuses[ownerSlot].PracticeIndex;

                var owner = Utilities.GetPlayerFromSlot(ownerSlot);

                if (owner == null || !owner.IsValid || owner.IsBot || owner.IsHLTV)
                {
                    Console.WriteLine($"[OpenPrefirePrac] The owner of bot ({playerOrBot.PlayerName}) is not right. This could be a bug.");
                    return HookResult.Continue;
                }

                if (targetNo >= _practices[practiceIndex].NumBots)         // Bots might be killed after their first time getting spawned, so as to move them to target spots.
                {
                    // Award the player.
                    if (owner.PawnIsAlive && owner.Pawn.Value != null)
                    {
                        owner.GiveNamedItem("item_assaultsuit");
                        RefillAmmo(owner);

                        if (_playerStatuses[ownerSlot].HealingMethod > 1)
                        {
                            var currentHp = owner.Pawn.Value.Health;
                            switch (_playerStatuses[ownerSlot].HealingMethod)
                            {
                                case 2:
                                    currentHp = currentHp + 25;
                                    break;
                                case 3:
                                    currentHp = 100;
                                    break;
                                case 4:
                                    currentHp = currentHp + 100;
                                    break;
                                case 5:
                                    currentHp = currentHp + 500;
                                    break;
                                default:
                                    currentHp = 100;
                                    break;
                            }
                            SetPlayerHealth(owner, currentHp);
                        }
                    }

                    // Per-target hit feedback (sound cue to the practicing player)
                    PlayHitFeedback(owner);

                    // Print progress (with a hitmarker to confirm the kill)
                    string tmpPractice = _translator!.Translate(owner, "map." + _mapName + "." + _practices[_playerStatuses[ownerSlot].PracticeIndex].PracticeName);
                    string tmpProgress = _translator!.Translate(owner, "practice.progress", _playerStatuses[ownerSlot].EnabledTargets.Count, _playerStatuses[ownerSlot].EnabledTargets.Count - targetNo + _playerStatuses[ownerSlot].Bots.Count - 1);
                    string content = $"\u2714\u2029{tmpPractice}\u2029{tmpProgress}";
                    owner.PrintToCenter(content);
                }

                // Kick unnecessary bots
                if (targetNo >= _playerStatuses[ownerSlot].EnabledTargets.Count)
                {
                    _ownerOfBots.Remove(playerOrBot.Slot);
                    _playerStatuses[ownerSlot].Bots.Remove(playerOrBot.Slot);
                    KickBot(playerOrBot.Slot);

                    if (_playerStatuses[ownerSlot].Bots.Count == 0)
                    {
                        // Practice finished.
                        owner.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White}{_translator!.Translate(owner, "practice.finish")}");
                        ExitPrefireMode(ownerSlot);
                    }
                }
                else
                {
                    // Fast respawn
                    AddTimer(0.35f, () => {
                        if (playerOrBot.IsValid && !playerOrBot.PawnIsAlive)
                        {
                            playerOrBot.Respawn();
                        }
                    });
                }
            }
            // else if (_playerCount > 0)
            // {
            //     // For unmanaged bots, kick them.
            //     Console.WriteLine($"[OpenPrefirePrac] Find an unmanaged bot ({playerOrBot.PlayerName}) dying, kick it.");
            //     KickBot(playerOrBot.Slot);
            // }
        }
        else
        {
            // For players: If some bots have already been kicked, add them back.
            if (!_playerStatuses.ContainsKey(playerOrBot.Slot))
                return HookResult.Continue;
            
            var practiceIndex = _playerStatuses[playerOrBot.Slot].PracticeIndex;
            var numBots = _playerStatuses[playerOrBot.Slot].Bots.Count;
            
            if (practiceIndex > -1 && numBots < _practices[practiceIndex].NumBots)
            {
                _playerStatuses[playerOrBot.Slot].Progress = 0;
                AddBot(playerOrBot, _practices[practiceIndex].NumBots - numBots);
            }
        }
        
        return HookResult.Continue;
    }

    public void OnRouteSelect(CCSPlayerController player, ChatMenuOption option)
    {
        int practiceNo = _playerStatuses[player.Slot].LocalizedPracticeNames[option.Text];
        StartPractice(player, practiceNo);
        CloseCurrentMenu(player);
    }

    public void OnForceExitPrefireMode(CCSPlayerController player, ChatMenuOption option)
    {
        ForceStopPractice(player);
        CloseCurrentMenu(player);
    }

    public void OpenMapMenu(CCSPlayerController player, ChatMenuOption option)
    {
        var mapMenu = new ChatMenu(_translator!.Translate(player, "mapmenu.title"));
        foreach (var map in _availableMaps)
        {
            mapMenu.AddMenuOption(map, OnMapSelected);
        }
        mapMenu.AddMenuOption(_translator!.Translate(player, "mainmenu.close_menu"), OnCloseMenu);

        player.PrintToChat("============ [OpenPrefirePrac] ============");
        MenuManager.OpenChatMenu(player, mapMenu);
        player.PrintToChat("===========================================");
    }

    public void OnMapSelected(CCSPlayerController player, ChatMenuOption option)
    {
        ChangeMap(player, option.Text);
    }

    public void OnOpenPracticeMenu(CCSPlayerController player, ChatMenuOption option)
    {
        OpenPracticeMenu(player);
    }

    public void OpenDifficultyMenu(CCSPlayerController player, ChatMenuOption option)
    {
        // Dynamically draw menu
        var difficultyMenu = new ChatMenu(_translator!.Translate(player, "difficulty.title"));
        _playerStatuses[player.Slot].LocalizedDifficultyNames.Clear();

        for (var i = 0; i < 5; i++)
        {
            var tmpLocalizedDifficultyName = _translator.Translate(player, $"difficulty.{i}");
            _playerStatuses[player.Slot].LocalizedDifficultyNames.Add(tmpLocalizedDifficultyName, i);
            difficultyMenu.AddMenuOption(tmpLocalizedDifficultyName, OnDifficultyChosen); // practice name here is split by space instead of underline. TODO: Use localized text.
        }
        difficultyMenu.AddMenuOption(_translator!.Translate(player, "mainmenu.close_menu"), OnCloseMenu);

        player.PrintToChat("============ [OpenPrefirePrac] ============");
        MenuManager.OpenChatMenu(player, difficultyMenu);
        player.PrintToChat("===========================================");
    }

    public void OnDifficultyChosen(CCSPlayerController player, ChatMenuOption option)
    {
        int difficultyNo = _playerStatuses[player.Slot].LocalizedDifficultyNames[option.Text];
        ChangeDifficulty(player, difficultyNo);
        CloseCurrentMenu(player);
    }

    public void OpenModeMenu(CCSPlayerController player, ChatMenuOption option)
    {
        var trainingModeMenu = new ChatMenu(_translator!.Translate(player, "modemenu.title"));
        _playerStatuses[player.Slot].LocalizedTrainingModeNames.Clear();

        for (var i = 0; i < 2; i++)
        {
            var tmpLocalizedTrainingModeName = _translator.Translate(player, $"modemenu.{i}");
            _playerStatuses[player.Slot].LocalizedTrainingModeNames.Add(tmpLocalizedTrainingModeName, i);
            trainingModeMenu.AddMenuOption(tmpLocalizedTrainingModeName, OnModeChosen);
        }
        trainingModeMenu.AddMenuOption(_translator!.Translate(player, "mainmenu.close_menu"), OnCloseMenu);

        player.PrintToChat("============ [OpenPrefirePrac] ============");
        MenuManager.OpenChatMenu(player, trainingModeMenu);
        player.PrintToChat("===========================================");
    }

    public void OnModeChosen(CCSPlayerController player, ChatMenuOption option)
    {
        var trainingModeNo = _playerStatuses[player.Slot].LocalizedTrainingModeNames[option.Text];
        ChangeTrainingMode(player, trainingModeNo);
        CloseCurrentMenu(player);
    }

    public void OpenLanguageMenu(CCSPlayerController player, ChatMenuOption option)
    {
        // No need for localization here.
        var languageMenu = new ChatMenu("Change language settings");

        languageMenu.AddMenuOption("English", OnLanguageChosen);
        languageMenu.AddMenuOption("Português", OnLanguageChosen);
        languageMenu.AddMenuOption("中文", OnLanguageChosen);
        languageMenu.AddMenuOption(_translator!.Translate(player, "mainmenu.close_menu"), OnCloseMenu);

        player.PrintToChat("============ [OpenPrefirePrac] ============");
        MenuManager.OpenChatMenu(player, languageMenu);
        player.PrintToChat("===========================================");
    }

    public void OnLanguageChosen(CCSPlayerController player, ChatMenuOption option)
    {
        switch (option.Text)
        {
            case "English":
                _translator!.UpdatePlayerCulture(player.SteamID, "EN");
                break;
            case "Português":
                _translator!.UpdatePlayerCulture(player.SteamID, "pt-BR");
                break;
            case "中文":
                _translator!.UpdatePlayerCulture(player.SteamID, "ZH");
                break;
            default:
                _translator!.UpdatePlayerCulture(player.SteamID, "EN");
                break;
        }

        player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White} {_translator!.Translate(player, "languagemenu.set")}");
        CloseCurrentMenu(player);
    }

    public void OnCloseMenu(CCSPlayerController player, ChatMenuOption option)
    {
        CloseCurrentMenu(player);
    }

    public void OpenBotWeaponMenu(CCSPlayerController player, ChatMenuOption option)
    {
        // Dynamically draw menu
        var botWeaponMenu = new ChatMenu(_translator!.Translate(player, "weaponmenu.title"));

        botWeaponMenu.AddMenuOption(_translator!.Translate(player, "weaponmenu.random"), OnBotWeaponChosen);
        botWeaponMenu.AddMenuOption("UMP-45", OnBotWeaponChosen);
        botWeaponMenu.AddMenuOption("AK47", OnBotWeaponChosen);
        botWeaponMenu.AddMenuOption("SSG08", OnBotWeaponChosen);
        botWeaponMenu.AddMenuOption("AWP", OnBotWeaponChosen);

        botWeaponMenu.AddMenuOption(_translator!.Translate(player, "mainmenu.close_menu"), OnCloseMenu);

        player.PrintToChat("============ [OpenPrefirePrac] ============");
        MenuManager.OpenChatMenu(player, botWeaponMenu);
        player.PrintToChat("===========================================");
    }

    public void OnBotWeaponChosen(CCSPlayerController player, ChatMenuOption option)
    {
        int botWeaponChoice = -1;

        switch (option.Text)
        {
            case "UMP-45":
                botWeaponChoice = 1;
                break;
            case "AK47":
                botWeaponChoice = 2;
                break;
            case "SSG08":
                botWeaponChoice = 3;
                break;
            case "AWP":
                botWeaponChoice = 4;
                break;
            default:
                botWeaponChoice = 0;
                break;
        }

        SetBotWeapon(player, botWeaponChoice);

        CloseCurrentMenu(player);
    }

    private void LoadPractice()
    {
        Console.WriteLine($"[OpenPrefirePrac] Loading practices for map {_mapName}.");
        var practiceFiles = new List<string>(Directory.EnumerateFiles($"{ModuleDirectory}/maps/{_mapName}"));
        _practices.Clear();
        _practiceNameToId.Clear();
        _pracConflictCount.Clear();
        for (var i = 0; i < practiceFiles.Count; i++)
        {
            var practiceName = practiceFiles[i].Substring(practiceFiles[i].LastIndexOf(Path.DirectorySeparatorChar) + 1).Split(".")[0];
            _practices.Add(new PrefirePractice(ModuleDirectory, _mapName, practiceName));
            _practiceNameToId.Add(practiceName, i);
            _pracConflictCount.Add(i, 0);
            Console.WriteLine($"[OpenPrefirePrac] {_mapName} {practiceName} Loaded.");
        }
    }
    
    private void ExitPrefireMode(int playerSlot)
    {
        UnsetPrefireMode(playerSlot);

        if (_playerCount == 0)
        {
            RestoreConvars();
        }
    }

    private void ResetBots(int slot)
    {
        _playerStatuses[slot].Progress = 0;

        List<int> botsToDelete = new List<int>();

        // for (var i = 0; i < _playerStatuses[player].Bots.Count; i++)
        foreach (var botSlot in _playerStatuses[slot].Bots)
        {
            // var bot = _playerStatuses[player].Bots[i];
            var bot = Utilities.GetPlayerFromSlot(botSlot);

            if (bot == null || !bot.IsValid)
            {
                Console.WriteLine($"[OpenPrefirePrac] Error: Player has an invalid bot. Unmanage it.");
                botsToDelete.Add(botSlot);
                continue;
            }

            if (bot.PawnIsAlive)
            {
                KillBot(bot);
            }
        }

        AddTimer(3f, () => {
            foreach (int bot in botsToDelete)
            {
                // _playerStatuses[slot].Bots.Remove(bot);
                // _ownerOfBots.Remove(bot);
                KickBot(bot);
            }
        });
    }

    private void SetupPrefireMode(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || !_playerStatuses.ContainsKey(player.Slot))
            return;

        var practiceNo = _playerStatuses[player.Slot].PracticeIndex;

        // Practice may have been torn down (e.g. round reset) before this
        // delayed setup fired. Bail out instead of indexing _practices[-1].
        if (practiceNo < 0 || practiceNo >= _practices.Count)
        {
            Console.WriteLine($"[OpenPrefirePrac] Skipping setup: practice index invalid ({practiceNo}).");
            return;
        }

        // Targets are generated in StartPractice (before bots spawn). Don't
        // regenerate here, or we'd re-randomize against already-placed bots.
        ResetBots(player.Slot);

        AddTimer(0.2f, () => MovePlayer(player, false, _practices[practiceNo].Player.Position, _practices[practiceNo].Player.Rotation));
        AddTimer(0.3f, () => EquipPlayer(player));
        AddTimer(0.35f, () => SetMoney(player, 60000));
        if (_playerStatuses[player.Slot].HealingMethod == 1 || _playerStatuses[player.Slot].HealingMethod == 5)
        {
            AddTimer(0.4f, () => SetPlayerHealth(player, 500));
        }
        else
        {
            AddTimer(0.4f, () => SetPlayerHealth(player, 100));         // in case player got injured by bots before teleport
        }
    }

    private void RemoveBots(int slot)
    {
        List<int> botsToKick = new List<int>();

        foreach (int botSlot in _playerStatuses[slot].Bots)
        {
            var bot = Utilities.GetPlayerFromSlot(botSlot);

            if (bot == null || bot.IsValid)
            {
                botsToKick.Add(botSlot);
            }
            else
            {
                Console.WriteLine($"[OpenPrefirePrac] Trying to kick an invalid bot.");
            }
        }

        foreach (int botSlot in botsToKick)
        {
            KickBot(botSlot);
        }

        _playerStatuses[slot].Progress = 0;
    }

    private void AddBot(CCSPlayerController player, int numberOfBots)
    {
        Console.WriteLine($"[OpenPrefirePrac] Creating {numberOfBots} bots.");

        // Test a new method of adding bots
        if (_botRequests.ContainsKey(player.Slot))
        {
            _botRequests[player.Slot] = numberOfBots;
        }
        else
        {
            _botRequests.Add(player.Slot, numberOfBots);
        }

        // Bots join the opposing team. They are added by raising bot_quota to exactly the number of bots
        // the practices need, instead of one bot_add per bot: in CS2 every bot_add also raises bot_quota,
        // so the engine added extra bots to meet the quota, the plugin kicked them as unmanaged, and the
        // engine re-added them - a join/kick loop that froze clients.
        if (player.TeamNum == (byte)CsTeam.CounterTerrorist)
            Server.ExecuteCommand("bot_join_team T");
        else if (player.TeamNum == (byte)CsTeam.Terrorist)
            Server.ExecuteCommand("bot_join_team CT");
        else
            return;

        SyncBotQuota();
    }

    /// <summary>
    /// Keeps bot_quota equal to the bots the practices own plus the ones still requested, so the engine
    /// never adds a bot nobody asked for. Only while someone practices; RestoreConvars puts the server's
    /// own bot_quota back afterwards.
    /// </summary>
    private void SyncBotQuota()
    {
        if (_playerCount <= 0)
            return;

        var wanted = _ownerOfBots.Count + _botRequests.Values.Sum();
        Server.ExecuteCommand($"bot_quota_mode normal; bot_quota {wanted}");
    }

    private void MovePlayer(CCSPlayerController? player, bool crouch, Vector pos, QAngle ang)
    {
        if (player == null || !player.IsValid || !player.PawnIsAlive || player.PlayerPawn.Value == null)
        {
            return;
        }

        // Console.WriteLine($"[OpenPrefirePrac] DEBUG: {player.PlayerName} moved to spawn point.");

        // Only bot can crouch
        if (player.IsBot)
        {
            var movementService = new CCSPlayer_MovementServices(player.PlayerPawn.Value.MovementServices!.Handle);
            if (crouch)
            {
                AddTimer(0.05f, () => movementService.DuckAmount = 1);
                AddTimer(0.1f, () => player.PlayerPawn.Value.Bot!.IsCrouching = true);
            }
            else
            {
                AddTimer(0.05f, () => movementService.DuckAmount = 0);
                AddTimer(0.1f, () => player.PlayerPawn.Value.Bot!.IsCrouching = false);
            }
        }
        
        player.PlayerPawn.Value.Teleport(pos, ang, Vector.Zero);
    }

    private void FreezeBot(CCSPlayerController? bot)
    {
        // Console.WriteLine($"[OpenPrefirePrac] DEBUG: Trying to freeze a bot.");
        if (bot != null &&
            bot is { IsValid: true, IsBot: true, IsHLTV: false, PawnIsAlive: true } 
            && bot.PlayerPawn.Value != null
        )
        {
            // Console.WriteLine($"[OpenPrefirePrac] DEBUG: Bot {bot.PlayerName} freezed.");

            // bot.PlayerPawn.Value.MoveType = MoveType_t.MOVETYPE_OBSOLETE;
            bot.PlayerPawn.Value.MoveType = MoveType_t.MOVETYPE_VPHYSICS;
            Schema.SetSchemaValue(bot.PlayerPawn.Value.Handle, "CBaseEntity", "m_nActualMoveType", 5);
            Utilities.SetStateChanged(bot.PlayerPawn.Value, "CBaseEntity", "m_MoveType");
        }
    }

    private void EquipPlayer(CCSPlayerController player)
    {
        if (player == null || !player.PawnIsAlive || player.PlayerPawn.Value == null)
            return;
        
        if (_defaultPlayerSettings!.EquipPlayer == 1)
        {
            player.RemoveWeapons();

            // Give weapons and items
            player.GiveNamedItem("weapon_ak47");
            player.GiveNamedItem("weapon_deagle");
            player.GiveNamedItem("weapon_knife");
            player.GiveNamedItem("weapon_flashbang");
            player.GiveNamedItem("weapon_flashbang");
            player.GiveNamedItem("weapon_smokegrenade");
            player.GiveNamedItem("item_assaultsuit");

            // Switch to main weapon
            player.ExecuteClientCommand("slot1");
        }
        else
        {
            player.GiveNamedItem("weapon_knife");
            player.GiveNamedItem("item_assaultsuit");
        }
    }

    private static void SetPlayerHealth(CCSPlayerController player, int hp)
    {
        if (player == null || !player.PawnIsAlive || player.Pawn.Value == null || hp < 0)
            return;
        
        // Console.WriteLine($"[OpenPrefirePrac] DEBUG: Setup player {player.PlayerName} with health.");

        if (hp > 100)
            player.Pawn.Value.MaxHealth = hp;
        player.Pawn.Value.Health = hp;
        Utilities.SetStateChanged(player.Pawn.Value, "CBaseEntity", "m_iHealth");
    }

    private void GenerateRandomPractice(int playerSlot)
    {
        _playerStatuses[playerSlot].EnabledTargets.Clear();
        var practiceNo = _playerStatuses[playerSlot].PracticeIndex;
        
        for (var i = 0; i < _practices[practiceNo].Targets.Count; i++)
            _playerStatuses[playerSlot].EnabledTargets.Add(i);

        if (_playerStatuses[playerSlot].TrainingMode == 0)
        {
            // 0: Use part of the targets.
            var numTargets = (int)(_practices[practiceNo].SpawnRatio * _practices[practiceNo].Targets.Count);

            var numToRemove = _practices[practiceNo].Targets.Count - numTargets;
            for (var i = 0; i < numToRemove; i++)
                _playerStatuses[playerSlot].EnabledTargets.RemoveAt(Random.Shared.Next(_playerStatuses[playerSlot].EnabledTargets.Count));
        }
        // 1: Use all of the targets.
    }

    private void CreateGuidingLine(int playerSlot)
    {
        var practiceNo = _playerStatuses[playerSlot].PracticeIndex;

        if (practiceNo < 0 || practiceNo >= _practices.Count)
        {
            Console.WriteLine($"[OpenPrefirePrac] Error when creating guiding line. Current practice_no illegal. (practice_no = {practiceNo})");
            return;
        }

        if (_practices[practiceNo].GuidingLine.Count < 2)
            return;

        // Draw beams
        for (int i = 0; i < _practices[practiceNo].GuidingLine.Count - 1; i++)
        {
            var beam = DrawBeam(_practices[practiceNo].GuidingLine[i], _practices[practiceNo].GuidingLine[i + 1]);

            if (beam == null)
                return;

            _playerStatuses[playerSlot].Beams.Add(beam);
        }

        UpdateGuidingLineTransmit();
    }

    // Each practicing player's guiding line is a chain of beams, and every client drew every player's
    // lines. Send each line only to its owner. The CheckTransmit listener runs every tick for every
    // recipient, so it is only registered while at least one line exists.
    private Listeners.CheckTransmit? _guidingLineTransmitHandler;

    private void UpdateGuidingLineTransmit()
    {
        bool anyLines = _playerStatuses.Values.Any(status => status.Beams.Count > 0);
        if (anyLines && _guidingLineTransmitHandler == null)
        {
            _guidingLineTransmitHandler = OnGuidingLineCheckTransmit;
            RegisterListener(_guidingLineTransmitHandler);
        }
        else if (!anyLines && _guidingLineTransmitHandler != null)
        {
            RemoveListener(_guidingLineTransmitHandler);
            _guidingLineTransmitHandler = null;
        }
    }

    private void OnGuidingLineCheckTransmit(CCheckTransmitInfoList infoList)
    {
        try
        {
            foreach ((CCheckTransmitInfo info, CCSPlayerController? player) in infoList)
            {
                if (player == null || !player.IsValid)
                    continue;

                var transmitEntities = info.TransmitEntities;
                foreach (var (ownerSlot, status) in _playerStatuses)
                {
                    if (ownerSlot == player.Slot || status.Beams.Count == 0)
                        continue;

                    foreach (var beam in status.Beams)
                    {
                        if (beam != null && beam.IsValid)
                            transmitEntities.Remove(beam);
                    }
                }
            }
        }
        catch (Exception e)
        {
            // Never throw out of the transmit path.
            Console.WriteLine($"[OpenPrefirePrac] Guiding line transmit: {e.Message}");
        }
    }

    private void DeleteGuidingLine(int playerSlot)
    {
        foreach (var beam in _playerStatuses[playerSlot].Beams)
        {
            if (beam == null || !beam.IsValid)
                continue;

            try
            {
                beam.Remove();
            }
            catch (Exception e)
            {
                // Entity may already be freed by the engine (e.g. round restart).
                Console.WriteLine($"[OpenPrefirePrac] Error when deleting guiding line. {e.Message}");
            }
        }

        _playerStatuses[playerSlot].Beams.Clear();
        UpdateGuidingLineTransmit();
    }

    private static CBeam? DrawBeam(Vector startPos, Vector endPos)
    {
        var beam = Utilities.CreateEntityByName<CBeam>("beam");
        if (beam == null)
        {
            // Failed to create beam
            Console.WriteLine($"[OpenPrefirePrac] Failed to create beam. Start position: {startPos}, end position: {endPos}");
            return null;
        }

        beam.Render = System.Drawing.Color.Blue;
        beam.Width = 2.0f;

        beam.Teleport(startPos, QAngle.Zero, Vector.Zero);
        beam.EndPos.X = endPos.X;
        beam.EndPos.Y = endPos.Y;
        beam.EndPos.Z = endPos.Z;
        beam.DispatchSpawn();
        Utilities.SetStateChanged(beam, "CBeam", "m_vecEndPos");

        // Console.WriteLine($"[OpenPrefirePrac] Created a beam. Start position: {startPos}, end position: {endPos}, entity index: {beam.Index}");
        return beam;
    }

    private void SaveConvars()
    {
        string[] boolConvarNames = [
            "bot_allow_grenades",
            "bot_allow_shotguns",
            "mp_autoteambalance",
            "sv_alltalk",
            "sv_full_alltalk",
            "bot_allow_pistols",
            "bot_allow_rifles",
            "bot_allow_snipers",
            "sv_auto_adjust_bot_difficulty",
            "mp_ignore_round_win_conditions",
        ];

        string[] intConvarNames = [
            "mp_buy_anywhere",
            "mp_free_armor",
            "mp_limitteams",
            // "sv_infinite_ammo",
            "mp_maxmoney",
            "mp_startmoney",
            "bot_difficulty",
            "custom_bot_difficulty",
            "mp_death_drop_gun",
            "mp_death_drop_grenade",
            "bot_quota",
            "mp_freezetime",
        ];

        string[] floatConvarNames = [
            "mp_respawn_immunitytime",
            "mp_buytime",
            "bot_max_vision_distance_override",
            "mp_roundtime",
            "mp_roundtime_defuse",
        ];

        string[] stringConvarNames = [
            "bot_quota_mode",
            "mp_ct_default_melee",
            "mp_t_default_melee",
            "mp_bot_ai_bt",
        ];

        try
        {
            // // sv_cheats
            // var sv_cheats = ConVar.Find("sv_cheats");
            // _serverStatus.sv_cheats = sv_cheats!.GetPrimitiveValue<bool>();

            // Bool convars
            foreach (var convarName in boolConvarNames)
            {
                var tmpConvar = ConVar.Find(convarName);
                if (tmpConvar != null)
                {
                    var value = tmpConvar.GetPrimitiveValue<bool>();
                    _serverStatus.BoolConvars.Add(convarName, value);
                    // Console.WriteLine($"[OpenPrefirePrac] {convarName}: {value}");
                }
            }

            // Int convars
            foreach (var convarName in intConvarNames)
            {
                var tmpConvar = ConVar.Find(convarName);
                if (tmpConvar != null)
                {
                    var value = tmpConvar.GetPrimitiveValue<int>();
                    _serverStatus.IntConvars.Add(convarName, value);
                    // Console.WriteLine($"[OpenPrefirePrac] {convarName}: {value}");
                }
            }

            // Float convars
            foreach (var convarName in floatConvarNames)
            {
                var tmpConvar = ConVar.Find(convarName);
                if (tmpConvar != null)
                {
                    var value = tmpConvar.GetPrimitiveValue<float>();
                    _serverStatus.FloatConvars.Add(convarName, value);
                    // Console.WriteLine($"[OpenPrefirePrac] {convarName}: {value}");
                }
            }

            // String convars
            foreach (var convarName in stringConvarNames)
            {
                var tmpConvar = ConVar.Find(convarName);
                if (tmpConvar != null)
                {
                    var value = tmpConvar.StringValue;
                    _serverStatus.StringConvars.Add(convarName, value);
                    // Console.WriteLine($"[OpenPrefirePrac] DEBUG {convarName}: {value}");
                }
            }
        }
        catch (System.Exception)
        {
            Console.WriteLine("[OpenPrefirePrac] Error reading convars.");
            throw;
        }

        Console.WriteLine("[OpenPrefirePrac] Values of convars saved.");
    }

    private void RestoreConvars()
    {
        // Bool convars
        foreach (var convar in _serverStatus.BoolConvars)
        {
            // var tmpConvar = ConVar.Find(convar.Key);
            // tmpConvar!.SetValue(convar.Value);
            Server.ExecuteCommand(convar.Key + " " + convar.Value.ToString());
        }
        _serverStatus.BoolConvars.Clear();

        // Int convars
        foreach (var convar in _serverStatus.IntConvars)
        {
            // var tmpConvar = ConVar.Find(convar.Key);
            // Somehow the following 2 methods don't work, just make up a command to implement this.
            Server.ExecuteCommand(convar.Key + " " + convar.Value.ToString());
            // tmpConvar!.GetPrimitiveValue<int>() = convar.Value;
            // tmpConvar!.SetValue(convar.Value);
        }
        _serverStatus.IntConvars.Clear();

        // Float convars
        foreach (var convar in _serverStatus.FloatConvars)
        {
            // var tmpConvar = ConVar.Find(convar.Key);
            // Somehow the following 2 methods don't work, just make up a command to implement this.
            Server.ExecuteCommand(convar.Key + " " + convar.Value.ToString());
            // tmpConvar!.GetPrimitiveValue<float>() = convar.Value;
            // tmpConvar!.SetValue(convar.Value);
        }
        _serverStatus.FloatConvars.Clear();

        // String convars
        foreach (var convar in _serverStatus.StringConvars)
        {
            // var tmpConvar = ConVar.Find(convar.Key);
            // tmpConvar!.StringValue = convar.Value;
            Server.ExecuteCommand(convar.Key + " \"" + convar.Value + "\"");
        }
        _serverStatus.StringConvars.Clear();

        // Restore sv_cheats
        // var sv_cheats = ConVar.Find("sv_cheats");
        // sv_cheats!.SetValue(_serverStatus.sv_cheats);
        // Server.ExecuteCommand("sv_cheats " + _serverStatus.sv_cheats.ToString());

        Console.WriteLine("[OpenPrefirePrac] Values of convars restored.");
    }

    private void SetupConvars()
    {
        // NOTE: Do NOT toggle tv_enable here. Changing tv_enable at runtime makes
        // CS2 rebuild the player/HLTV slot table ("maxplayers set to 64") and resets
        // server state (ss_active -> ss_dead), which drops the A2S query socket and
        // breaks external server queries (gamedig) for the whole practice session.
        // SourceTV is left to the server's launch/cfg config.
        // Server.ExecuteCommand("sv_cheats 1");
        Server.ExecuteCommand("bot_allow_grenades 0");
        Server.ExecuteCommand("bot_allow_shotguns 0");
        Server.ExecuteCommand("mp_autoteambalance 0");
        Server.ExecuteCommand("sv_alltalk 1");
        Server.ExecuteCommand("sv_full_alltalk 1");
        Server.ExecuteCommand("bot_allow_pistols 1");
        Server.ExecuteCommand("bot_allow_rifles 1");
        Server.ExecuteCommand("bot_allow_snipers 1");
        Server.ExecuteCommand("sv_auto_adjust_bot_difficulty 0");

        Server.ExecuteCommand("mp_buy_anywhere 1");
        Server.ExecuteCommand("mp_free_armor 2");
        Server.ExecuteCommand("mp_limitteams 0");
        // Server.ExecuteCommand("sv_infinite_ammo 1");
        Server.ExecuteCommand("mp_maxmoney 60000");
        Server.ExecuteCommand("mp_startmoney 60000");
        Server.ExecuteCommand("bot_difficulty 5");
        Server.ExecuteCommand("custom_bot_difficulty 5");
        Server.ExecuteCommand("mp_death_drop_gun 0");
        Server.ExecuteCommand("mp_death_drop_grenade 0");
        Server.ExecuteCommand("bot_quota 0");

        // Keep the round alive while practicing. Previously warmup did this job;
        // without it, wiping a team would end the round and tear down the practice.
        Server.ExecuteCommand("mp_ignore_round_win_conditions 1");
        Server.ExecuteCommand("mp_roundtime 60");
        Server.ExecuteCommand("mp_roundtime_defuse 60");
        Server.ExecuteCommand("mp_freezetime 2");

        Server.ExecuteCommand("mp_respawn_immunitytime -1");
        Server.ExecuteCommand("mp_buytime 9999");
        Server.ExecuteCommand("bot_max_vision_distance_override 99999");

        Server.ExecuteCommand("bot_quota_mode normal");
        Server.ExecuteCommand("mp_ct_default_melee \"\"");
        Server.ExecuteCommand("mp_t_default_melee \"\"");

        // Server.ExecuteCommand("bot_kick all");

        // Set behavior tree for bots (many thanks to 5EPlay)
        if (_defaultPlayerSettings!.BotAimLock == 2)
        {
            var behaviourTree = PrepareBehaviourTree();
            if (behaviourTree != null)
            {
                Server.ExecuteCommand($"mp_bot_ai_bt \"{behaviourTree}\"");
                Server.ExecuteCommand("mp_bot_ai_bt_clear_cache");
            }
        }

        // Kick unmanaged bots
        AddTimer(2f, () => {
            var players = Utilities.GetPlayers();
            List<CCSPlayerController> botsToKick = new List<CCSPlayerController>();
            foreach(var player in players)
            {
                if (player == null || !player.IsValid || player.IsHLTV || !player.IsBot)
                {
                    continue;
                }

                if (_ownerOfBots.ContainsKey(player.Slot))
                {
                    continue;
                }

                botsToKick.Add(player);
            }
            foreach (var bot in botsToKick)
            {
                KickBot(bot.Slot);
            }
            botsToKick.Clear();
        });

        Server.ExecuteCommand("bot_autodifficulty_threshold_high 5");
        Server.ExecuteCommand("bot_autodifficulty_threshold_low 5");
        Server.ExecuteCommand("sv_auto_adjust_bot_difficulty 0");
        Server.ExecuteCommand("weapon_auto_cleanup_time 1");       
        Server.ExecuteCommand("mp_roundtime 60");
        Server.ExecuteCommand("mp_roundtime_defuse 60");
        Server.ExecuteCommand("mp_freezetime 0");
        Server.ExecuteCommand("mp_team_intro_time 0");
        Server.ExecuteCommand("mp_ignore_round_win_conditions 1");
        Server.ExecuteCommand("mp_respawn_on_death_ct 1");
        Server.ExecuteCommand("mp_respawn_on_death_t 1");

        Console.WriteLine("[OpenPrefirePrac] Values of convars set.");
    }

    // The hard-mode behaviour tree files reference each other by a path fixed to
    // plugins/OpenPrefirePrac/resources/bt, so they broke whenever the plugin lived anywhere else
    // (plugins/disabled/OpenPrefirePrac on fshost, or a folder a customer moved over FTP). Copy them to
    // configs/plugins/OpenPrefirePrac/bt with the paths rewritten to that copy: configs/ never moves and
    // is never replaced by plugin updates. Returns the mp_bot_ai_bt path (relative to csgo/), or null.
    private const string BehaviourTreeShippedPath = "addons//counterstrikesharp//plugins//OpenPrefirePrac//resources//bt//";
    private const string BehaviourTreeConfigPath = "addons//counterstrikesharp//configs//plugins//OpenPrefirePrac//bt//";
    private string? _behaviourTreePath;

    private string? PrepareBehaviourTree()
    {
        if (_behaviourTreePath != null)
            return _behaviourTreePath;

        try
        {
            var csgo = Path.Combine(Server.GameDirectory, "csgo");
            var sourceDir = new[]
            {
                Path.Combine(ModuleDirectory, "resources", "bt"),
                // fshost keeps resources/bt in plugins/OpenPrefirePrac while the plugin is in plugins/disabled.
                Path.Combine(csgo, "addons", "counterstrikesharp", "plugins", "OpenPrefirePrac", "resources", "bt"),
            }.FirstOrDefault(dir => File.Exists(Path.Combine(dir, "hard_mode.kv3")));

            if (sourceDir == null)
            {
                Console.WriteLine("[OpenPrefirePrac] resources/bt/hard_mode.kv3 not found; hard mode bots use the default behaviour.");
                return null;
            }

            var targetDir = Path.Combine(csgo, "addons", "counterstrikesharp", "configs", "plugins", "OpenPrefirePrac", "bt");
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(sourceDir, "*.kv3"))
            {
                var text = File.ReadAllText(file).Replace(BehaviourTreeShippedPath, BehaviourTreeConfigPath);
                var target = Path.Combine(targetDir, Path.GetFileName(file));
                if (!File.Exists(target) || File.ReadAllText(target) != text)
                    File.WriteAllText(target, text);
            }

            _behaviourTreePath = BehaviourTreeConfigPath.Replace("//", "\\") + "hard_mode.kv3";
            return _behaviourTreePath;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[OpenPrefirePrac] Could not prepare the behaviour tree: {e.Message}");
            return null;
        }
    }

    private void RefillAmmo(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || !player.PawnIsAlive || player.PlayerPawn == null || player.PlayerPawn.Value == null || player.PlayerPawn.Value.WeaponServices == null)
        {
            return;
        }

        var weapons = player.PlayerPawn.Value.WeaponServices.MyWeapons;
        foreach (var weapon in weapons)
        {
            if (weapon != null && weapon.IsValid && weapon.Value != null && weapon.Value.DesignerName.Length != 0 && !weapon.Value.DesignerName.Contains("knife") && !weapon.Value.DesignerName.Contains("bayonet"))
            {
                int magAmmo = 999;
                int reservedAmmo = 999;
                switch (weapon.Value.DesignerName)
                {
                    case "weapon_ak47":
                    case "weapon_m4a1":         // M4A4
                        magAmmo = 31;
                        reservedAmmo = 90;
                        break;
                    case "weapon_m4a1_":         // M4A1_silencer
                        magAmmo = 21;
                        reservedAmmo = 80;
                        break;
                    case "weapon_deagle":
                        magAmmo = 8;
                        reservedAmmo = 35;
                        break;
                    case "weapon_flashbang":
                    case "weapon_smokegrenade":
                    case "weapon_decoy":
                    case "weapon_molotov":
                    case "weapon_incgrenade":
                        continue;
                    default:
                        magAmmo = 999;
                        reservedAmmo = 999;
                        break;
                }

                weapon.Value.Clip1 = magAmmo;
                Utilities.SetStateChanged(weapon.Value, "CBasePlayerWeapon", "m_iClip1");
                weapon.Value.ReserveAmmo[0] = reservedAmmo;
                Utilities.SetStateChanged(weapon.Value, "CBasePlayerWeapon", "m_pReserveAmmo");
            }
        }
    }

    private void KillBot(CCSPlayerController bot)
    {
        if (!bot.IsValid || !bot.IsBot || bot.IsHLTV || !bot.PawnIsAlive)
        {
            return;
        }

        bot.CommitSuicide(false, false);
    }

    private void StartPractice(CCSPlayerController player, int practiceIndex)
    {
        if (_botsDisabled)
        {
            player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White}{_translator!.Translate(player, "practice.nobots")}");
            return;
        }

        if (_playerCount == 0)
        {
            SaveConvars();
            SetupConvars();
            AddTimer(0.5f, () => BreakBreakables());
        }

        var previousPracticeIndex = _playerStatuses[player.Slot].PracticeIndex;

        if (previousPracticeIndex != practiceIndex)
        {
            // Check if selected practice route is compatible with other on-playing routes.
            if (_pracConflictCount[practiceIndex] > 0)
            {
                player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White}{_translator!.Translate(player, "practice.incompatible")}");
                return;
            }

            // Update practice status
            if (previousPracticeIndex > -1)
            {
                UnsetPrefireMode(player.Slot);
                DeleteGuidingLine(player.Slot);
            }
            
            _playerCount++;
            _playerStatuses[player.Slot].PracticeIndex = practiceIndex;

            // Generate targets BEFORE adding bots. Bots spawn almost immediately
            // after AddBot; if EnabledTargets is still empty when OnPlayerSpawn
            // fires, every bot falls into the "extra bot" branch and gets kicked,
            // tearing down the practice. (Warmup used to delay spawns past setup.)
            GenerateRandomPractice(player.Slot);

            AddTimer(1f, () => CreateGuidingLine(player.Slot));

            // Disable incompatible practices.
            for (var i = 0; i < _practices[practiceIndex].IncompatiblePractices.Count; i++)
            {
                if (_practiceNameToId.ContainsKey(_practices[practiceIndex].IncompatiblePractices[i]))
                {
                    var disabledPracticeNo = _practiceNameToId[_practices[practiceIndex].IncompatiblePractices[i]];
                    _pracConflictCount[disabledPracticeNo]++;
                }
            }
            _pracConflictCount[practiceIndex]++;

            // Setup practice
            AddBot(player, _practices[practiceIndex].NumBots);
        }
        else
        {
            // If some bots have already been kicked, add them back.
            var numRemainingBots = _playerStatuses[player.Slot].Bots.Count;
            
            if (numRemainingBots < _practices[practiceIndex].NumBots)
            {
                _playerStatuses[player.Slot].Progress = 0;
                AddBot(player, _practices[practiceIndex].NumBots - numRemainingBots);
            }
        }

        // Practice begin
        AddTimer(0.5f, () => SetupPrefireMode(player));
        var localizedPracticeName = _translator!.Translate(player, "map." + _mapName + "." + _practices[practiceIndex].PracticeName);
        player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White} {_translator.Translate(player, "practice.choose", localizedPracticeName)}");
        player.PrintToCenter(_translator.Translate(player, "practice.begin"));
    }

    private void ChangeMap(CCSPlayerController player, string mapName)
    {
        // Check if the map has practice routes
        if (!_availableMaps.Contains(mapName))
        {
            player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White}{_translator!.Translate(player, "mapmenu.not_available")}");
            return;
        }

        // Only allow change map when nobody is practicing.
        if (_playerCount == 0)
        {
            Server.ExecuteCommand($"changelevel {mapName}");
        }
        else
        {
            player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White}{_translator!.Translate(player, "mapmenu.busy")}");
        }
    }

    private void ChangeDifficulty(CCSPlayerController player, int difficultyNo)
    {
        _playerStatuses[player.Slot].HealingMethod = difficultyNo;
        var currentDifficulty = _translator!.Translate(player, $"difficulty.{difficultyNo}");
        player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White} {_translator!.Translate(player, "difficulty.set", currentDifficulty)}");
    }

    private void ChangeTrainingMode(CCSPlayerController player, int trainingMode)
    {
        _playerStatuses[player.Slot].TrainingMode = trainingMode;
        var currentTrainingMode = _translator!.Translate(player, $"modemenu.{trainingMode}");
        player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White} {_translator!.Translate(player, "modemenu.set", currentTrainingMode)}");
    }

    private void ForceStopPractice(CCSPlayerController player)
    {
        ExitPrefireMode(player.Slot);
        player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White}{_translator!.Translate(player, "practice.exit")}");
    }

    private void UnregisterCommand()
    {
        if (_command == null)
        {
            return;
        }

        CommandManager.RemoveCommand(_command);
        _command = null;
    }

    private void RegisterCommand()
    {
        if (_command != null)
        {
            UnregisterCommand();
        }

        _command = new CommandDefinition("css_prefire", "Command to bring up the main menu of OpenPrefirePrac.", (player, commandInfo) => {
            // This is a client only command
            if (player == null)
            {
                return;
            }

            // Command shortcuts
            if (commandInfo.ArgCount > 1)
            {
                switch (commandInfo.ArgByIndex(1))
                {
                    case "prac":
                        int choice = 0;
                        if (int.TryParse(commandInfo.ArgByIndex(2), out choice) && choice > 0 && choice <= _practices.Count)
                        {
                            StartPractice(player, choice - 1);
                            return;
                        }
                        player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White} {_translator!.Translate(player, "practice.help", _practices.Count)}");
                        return;
                    case "map":
                        string mapName = commandInfo.ArgByIndex(2);
                        ChangeMap(player, mapName);
                        return;
                    case "df":
                        int difficulty = 0;
                        if (int.TryParse(commandInfo.ArgByIndex(2), out difficulty) && difficulty > 0 && difficulty <= 6)
                        {
                            ChangeDifficulty(player, 6 - difficulty);
                            return;
                        }
                        player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White} {_translator!.Translate(player, "difficulty.help")}");
                        return;
                    case "mode":
                        string trainingMode = commandInfo.ArgByIndex(2);
                        switch (trainingMode)
                        {
                            case "full":
                                ChangeTrainingMode(player, 1);
                                return;
                            case "rand":
                                ChangeTrainingMode(player, 0);
                                return;
                            default:
                                player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White} {_translator!.Translate(player, "modemenu.help")}");
                                return;
                        }
                    case "bw":
                        string botWeapon = commandInfo.ArgByIndex(2);
                        switch (botWeapon)
                        {
                            case "rand":
                                SetBotWeapon(player, 0);
                                return;
                            case "ump":
                                SetBotWeapon(player, 1);
                                return;
                            case "ak":
                                SetBotWeapon(player, 2);
                                return;
                            case "sct":
                                SetBotWeapon(player, 3);
                                return;
                            case "awp":
                                SetBotWeapon(player, 4);
                                return;
                            default:
                                player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White} {_translator!.Translate(player, "weaponmenu.help")}");
                                return;
                        }
                    case "lang":
                        string language = commandInfo.ArgByIndex(2);
                        switch (language)
                        {
                            case "en":
                                _translator!.UpdatePlayerCulture(player.SteamID, "EN");
                                break;
                            case "pt":
                                _translator!.UpdatePlayerCulture(player.SteamID, "pt-BR");
                                break;
                            case "zh":
                                _translator!.UpdatePlayerCulture(player.SteamID, "ZH");
                                break;
                            default:
                                player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White} {_translator!.Translate(player, "languagemenu.help")}");
                                return;
                        }
                        player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White} {_translator!.Translate(player, "languagemenu.set")}");
                        return;
                    case "exit":
                        ForceStopPractice(player);
                        return;
                    case "rec":
                        HandleRecordCommand(player, commandInfo);
                        return;
                    case "help":
                        player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White} {_translator!.Translate(player, "mainmenu.help", _practices.Count)}");
                        return;
                    // case "test":
                    //     SetMoney(player, 0);
                    //     AddTimer(5f, () => SetMoney(player, 123));
                    //     return;
                    default:
                        player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White} {_translator!.Translate(player, "mainmenu.help", _practices.Count)}");
                        break;
                }
            }

            // Draw the menu
            var mainMenu = new ChatMenu(_translator!.Translate(player, "mainmenu.title"));

            // 1 Practice menu
            mainMenu.AddMenuOption(_translator.Translate(player, "mainmenu.practice"), OnOpenPracticeMenu);

            // 2 Map menu
            mainMenu.AddMenuOption(_translator.Translate(player, "mainmenu.map"), OpenMapMenu);

            // 3 Difficulty menu
            string currentDifficulty = _translator.Translate(player, $"difficulty.{_playerStatuses[player.Slot].HealingMethod}");
            mainMenu.AddMenuOption(_translator.Translate(player, "mainmenu.difficulty", currentDifficulty), OpenDifficultyMenu);
            
            // 4 Training mode menu
            string currentTrainingMode = _translator.Translate(player, $"modemenu.{_playerStatuses[player.Slot].TrainingMode}");
            mainMenu.AddMenuOption(_translator.Translate(player, "mainmenu.mode", currentTrainingMode), OpenModeMenu);

            // 5 Bot weapon menu
            string currentBotWeapon = "";
            switch (_playerStatuses[player.Slot].BotWeapon)
            {
                case 0:
                    currentBotWeapon = _translator!.Translate(player, "weaponmenu.random");
                    break;
                case 1:
                    currentBotWeapon = "UMP-45";
                    break;
                case 2:
                    currentBotWeapon = "AK47";
                    break;
                case 3:
                    currentBotWeapon = "AWP";
                    break;
                default:
                    break;
            }
            mainMenu.AddMenuOption(_translator!.Translate(player, "mainmenu.bot_weapon", currentBotWeapon), OpenBotWeaponMenu);

            // 6 Language menu
            mainMenu.AddMenuOption("Language preference", OpenLanguageMenu);

            // 7 (Optional) End practicing button — show whenever a practice is
            // active (index 0 is a valid route, so use > -1, not > 0).
            if (_playerStatuses[player.Slot].PracticeIndex > -1)
            {
                mainMenu.AddMenuOption(_translator.Translate(player, "mainmenu.exit"), OnForceExitPrefireMode);
            }

            // 8 Close menu button.
            mainMenu.AddMenuOption(_translator!.Translate(player, "mainmenu.close_menu"), OnCloseMenu);

            player.PrintToChat("============ [OpenPrefirePrac] ============");
            MenuManager.OpenChatMenu(player, mainMenu);
            player.PrintToChat(_translator.Translate(player, "mainmenu.shortcut_prompt"));
            player.PrintToChat("===========================================");
        });

        CommandManager.RegisterCommand(_command);
    }

    private void CloseCurrentMenu(CCSPlayerController player)
    {
        MenuManager.CloseActiveMenu(player);
        player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White} {_translator!.Translate(player, "mainmenu.menu_closed")}");
    }

    private void SetBotWeapon(CCSPlayerController player, int botWeapon)
    {
        _playerStatuses[player.Slot].BotWeapon = botWeapon;

        string weaponName = "";
        switch (botWeapon)
        {
            case 0:
                weaponName = _translator!.Translate(player, "weaponmenu.random");
                break;
            case 1:
                weaponName = "UMP-45";
                break;
            case 2:
                weaponName = "AK47";
                break;
            case 3:
                weaponName = "SSG08";
                break;
            case 4:
                weaponName = "AWP";
                break;
        }

        player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White} {_translator!.Translate(player, "weaponmenu.set", weaponName)}");
    }

    private void OpenPracticeMenu(CCSPlayerController player)
    {
        // Dynamically draw menu
        var practiceMenu = new ChatMenu(_translator!.Translate(player, "practicemenu.title"));
        _playerStatuses[player.Slot].LocalizedPracticeNames.Clear();

        // Add menu options for practices
        for (var i = 0; i < _practices.Count; i++)
        {
            if (_pracConflictCount[i] == 0)
            {
                var tmpLocalizedPracticeName = _translator.Translate(player, $"map.{_mapName}.{_practices[i].PracticeName}");
                _playerStatuses[player.Slot].LocalizedPracticeNames.Add(tmpLocalizedPracticeName, i);
                practiceMenu.AddMenuOption(tmpLocalizedPracticeName, OnRouteSelect); // practice name here is split by space instead of underline. TODO: Use localized text.
            }
        }
        int practiceNo = _playerStatuses[player.Slot].PracticeIndex;
        if (practiceNo > -1)
        {
            var tmpLocalizedPracticeName = _translator.Translate(player, $"map.{_mapName}.{_practices[practiceNo].PracticeName}");
            _playerStatuses[player.Slot].LocalizedPracticeNames.Add(tmpLocalizedPracticeName, practiceNo);
            practiceMenu.AddMenuOption(tmpLocalizedPracticeName, OnRouteSelect);
        }

        practiceMenu.AddMenuOption(_translator!.Translate(player, "mainmenu.close_menu"), OnCloseMenu);

        player.PrintToChat("============ [OpenPrefirePrac] ============");
        MenuManager.OpenChatMenu(player, practiceMenu);
        player.PrintToChat("===========================================");
    }

    private void UnsetPrefireMode(int playerSlot)
    {
        var previousPracticeNo = _playerStatuses[playerSlot].PracticeIndex;
        if (previousPracticeNo > -1)
        {
            RemoveBots(playerSlot);
            DeleteGuidingLine(playerSlot);

            // Enable disabled practice routes
            for (var i = 0; i < _practices[previousPracticeNo].IncompatiblePractices.Count; i++)
            {
                if (_practiceNameToId.TryGetValue(_practices[previousPracticeNo].IncompatiblePractices[i], out var value))
                {
                    _pracConflictCount[value]--;
                }
            }
            _pracConflictCount[previousPracticeNo]--;

            _playerStatuses[playerSlot].PracticeIndex = -1;
            _playerCount--;

            // patch: check and remove request of bots in case something goes wrong resulting in a stuck request
            if (_botRequests.ContainsKey(playerSlot))
            {
                _botRequests.Remove(playerSlot);
            }
        }
    }

    private void SetMoney(CCSPlayerController player, int money)
    {
        var moneyServices = player.InGameMoneyServices;
        if (moneyServices == null)
        {
            return;
        }
        
        moneyServices.Account = money;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
    }

    private void PrintProgress()
    {
        var players = Utilities.GetPlayers();

        foreach (var player in players)
        {
            if (!player.IsValid || player.IsBot || player.IsHLTV || !_playerStatuses.ContainsKey(player.Slot) || _playerStatuses[player.Slot].PracticeIndex < 0)
            {
                continue;
            }

            // If player is practicing, print timer
            string tmpPractice = _translator!.Translate(player, "map." + _mapName + "." + _practices[_playerStatuses[player.Slot].PracticeIndex].PracticeName);
            string tmpProgress = _translator!.Translate(player, "practice.progress", _playerStatuses[player.Slot].EnabledTargets.Count, _playerStatuses[player.Slot].EnabledTargets.Count - _playerStatuses[player.Slot].Progress + _playerStatuses[player.Slot].Bots.Count);
            string content = $"{tmpPractice}\u2029{tmpProgress}";

            player.PrintToCenter(content);
        }
    }

    private void PlayHitFeedback(CCSPlayerController owner)
    {
        var sound = _defaultPlayerSettings!.HitFeedbackSound;

        if (string.IsNullOrEmpty(sound) || owner.PlayerPawn.Value == null)
        {
            return;
        }

        // Play the cue only to the practicing player.
        owner.PlayerPawn.Value.EmitSound(sound, new RecipientFilter(owner));
    }

    private void KickBot(int botSlot)
    {

        if (_ownerOfBots.ContainsKey(botSlot))
        {
            int ownerSlot = _ownerOfBots[botSlot];
            if (_playerStatuses[ownerSlot].Bots.Contains(botSlot))
            {
                _playerStatuses[ownerSlot].Bots.Remove(botSlot);
            }
            _ownerOfBots.Remove(botSlot);
        }

        var bot = Utilities.GetPlayerFromSlot(botSlot);

        if (bot == null || !bot.IsValid || !bot.IsBot)
        {
            return;
        }

        Server.ExecuteCommand($"bot_kick {bot.PlayerName}");
        Console.WriteLine($"[OpenPrefirePrac] Exec command: bot_kick {bot.PlayerName}");

        // A kicked bot must not be refilled by bot_quota.
        SyncBotQuota();
    }

    // Thanks to B3none
    // Code borrowed from cs2-retake/RetakesPlugin/Modules/Managers/BreakerManager.cs
    private void BreakBreakables()
    {
        // Enable this feature only on nuke and mirage. (mirage is disabled because of the crash issue on Windows)
        if (Server.MapName != "de_nuke") // && Server.MapName != "de_mirage")
        {
            Console.WriteLine($"[OpenPrefirePrac] Map {Server.MapName} doesn't have breakables to break.");
            return;
        }

        Console.WriteLine($"[OpenPrefirePrac] Map {Server.MapName} have breakables to break.");

        // Enable certain breakables on certain maps to avoid game crash
        List<string> enabled_breakables =
        [
            // Common breakables
            "func_breakable",
            "func_breakable_surf",
            "prop.breakable.01",
            "prop.breakable.02",
        ];

        if (Server.MapName == "de_nuke")
        {
            enabled_breakables.Add("prop_door_rotating");
            enabled_breakables.Add("prop_dynamic");
        }

        if (Server.MapName == "de_mirage")
        {
            enabled_breakables.Add("prop_dynamic");
        }

        // Console.WriteLine($"[OpenPrefirePrac] DEBUG: Have breakables: {enabled_breakables}");

        // Loop to find breakables
        CEntityIdentity ?pEntity = new CEntityIdentity(EntitySystem.FirstActiveEntity);
        while (pEntity != null && pEntity.Handle != IntPtr.Zero)
        {
            if (!enabled_breakables.Contains(pEntity.DesignerName))
            {
                pEntity = pEntity.Next;
                continue;
            }

            switch (pEntity.DesignerName)
            {
                case "func_breakable":
                case "func_breakable_surf":
                case "prop.breakable.01":
                case "prop.breakable.02":
                case "prop_dynamic":
                    CBreakable breakableEntity = new PointerTo<CBreakable>(pEntity.Handle).Value;
                    if (breakableEntity.IsValid)
                    {
                        breakableEntity.AcceptInput("Break");
                    }
                    break;
                case "func_button":
                    CBaseButton button = new PointerTo<CBaseButton>(pEntity.Handle).Value;
                    if (button.IsValid)
                    {
                        button.AcceptInput("Kill");
                    }
                    break;
                case "prop_door_rotating":
                    CPropDoorRotating propDoorRotating = new PointerTo<CPropDoorRotating>(pEntity.Handle).Value;
                    if (propDoorRotating.IsValid)
                    {
                        propDoorRotating.AcceptInput("Open");
                    }
                    break;
                default:
                    break;
            }

            // Get next entity
            pEntity = pEntity.Next;
        }
    }

    // Runs on the game thread once per tick. Everything in here is on the server's frame
    // budget (15.6ms at 64 tick), so it is kept allocation-free: the old version built two
    // Vectors, a QAngle and a Server.NextFrame closure per bot per tick, which with a handful
    // of players practising is thousands of gen0 objects a second and the GC pauses that come
    // with them. The work is also done inline rather than deferred to the next frame — this
    // listener already runs inside the game frame, so the deferral bought nothing but garbage.
    private void OnTickHandler()
    {
        if (_defaultPlayerSettings!.BotAimLock != 1)
        {
            return;
        }

        // Snapshot the slots: the teleports below can run engine code that touches
        // _playerStatuses, which would invalidate a live enumerator.
        _aimLockSlots.Clear();
        foreach (var slot in _playerStatuses.Keys)
        {
            _aimLockSlots.Add(slot);
        }

        foreach (int playerSlot in _aimLockSlots)
        {
            if (!_playerStatuses.TryGetValue(playerSlot, out var status))
            {
                continue;
            }

            var player = Utilities.GetPlayerFromSlot(playerSlot);

            if (player == null || !player.IsValid || !player.PawnIsAlive || player.PlayerPawn.Value == null)
            {
                continue;
            }

            // Aimlock bots
            var ownerOrigin = player.PlayerPawn.Value.AbsOrigin!;
            float ownerEyeX = ownerOrigin.X;
            float ownerEyeY = ownerOrigin.Y;
            // Eye height (m_vecViewOffset), not ViewmodelOffsetZ - that's the weapon model's offset, which
            // made bots aim at about the player's feet.
            float ownerEyeZ = ownerOrigin.Z + player.PlayerPawn.Value.ViewOffset.Z;

            foreach (var botSlot in status.Bots)
            {
                var bot = Utilities.GetPlayerFromSlot(botSlot);

                if (bot == null || !bot.IsValid || !bot.PawnIsAlive || bot.PlayerPawn.Value == null)
                {
                    continue;
                }

                var botPawn = bot.PlayerPawn.Value;
                var botOrigin = botPawn.AbsOrigin!;

                // calculate angle
                float deltaX = ownerEyeX - botOrigin.X;
                float deltaY = ownerEyeY - botOrigin.Y;
                float deltaZ = ownerEyeZ - (botOrigin.Z + botPawn.ViewOffset.Z);
                double yaw = 180 * Math.Atan2(deltaY, deltaX) / Math.PI;
                double tmp = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
                double pitch = 180 * Math.Atan2(-1 * deltaZ, tmp) / Math.PI;

                // Turn the body towards the player on every tick, upright (yaw only). It used to turn only
                // while the height difference was under 15 degrees, so bots above or below the player
                // (heaven, boost, balcony...) kept facing the wrong way and never shot. The vertical aim
                // goes through EyeAngles below.
                // Only when it is noticeably off, though: every Teleport (even angles only) resets the bot's
                // lag-compensation history ("No valid positions in history for BacktrackPlayer"), so
                // turning every bot on every tick broke hit registration on them.
                var currentYaw = botPawn.AbsRotation?.Y ?? float.NaN;
                var yawError = Math.Abs(Math.IEEERemainder(yaw - currentYaw, 360.0));
                if (double.IsNaN(yawError) || yawError > AimLockYawTolerance)
                {
                    _aimLockAngle.X = 0;
                    _aimLockAngle.Y = (float)yaw;
                    _aimLockAngle.Z = 0;
                    botPawn.Teleport(null, _aimLockAngle, null);
                }

                botPawn.EyeAngles.X = (float)pitch;
                botPawn.EyeAngles.Y = (float)yaw;
            }
        }
    }

}
