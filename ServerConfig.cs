using System.Text.Json;

namespace OpenPrefirePrac;

public class DefaultConfig
{
    public int Difficulty { get; set; } = 3;

    public int TrainingMode { get; set; } = 0;

    public int BotWeapon {get; set; } = 0;

    /*
     * 0 = disabled.
     * 1 = css based aim lock.
     * 2 = behavior tree based aim lock (hard).
    */
    public int BotAimLock {get; set; } = 1;

    public int EquipPlayer { get; set; } = 1;

    /*
     * Soundevent played to the practicing player each time they kill a target.
     * Empty string disables the sound (visual hitmarker still shows).
     * Override in default_cfg.json if this soundevent isn't present on your server.
    */
    public string HitFeedbackSound { get; set; } = "Buttons.snd9";

    private string _moduleDirectory = "";

    public DefaultConfig()
    {
        // DeserializeConstructor
    }

    public DefaultConfig(string moduleDirectory)
    {
        _moduleDirectory = moduleDirectory;
    }
    
    private const string ConfigFileName = "default_cfg.json";

    // Written on first start. Values match the property defaults above.
    private const string ConfigTemplate = """
{
    // Difficulty:
    //   0: No healing
    //   1: Init hp 500 with no healing
    //   2: +25hp for each kill
    //   3: Reheal to 100hp after a kill
    //   4: +100hp for each kill
    //   5: +500hp for each kill
    "Difficulty": 3,
    // Training Mode:
    //   0: Random mode, randomly spawn some targets.
    //   1: Full mode, all targets.
    "TrainingMode": 0,
    // Bot Weapon:
    //   0: Bots buy weapons randomly.
    //   1: Bots use UMP45.
    //   2: Bots use AK47.
    //   3: Bots use Scout.
    //   4: Bots use AWP.
    "BotWeapon": 0,
    // Aim lock for bots:
    //   0: CS2's native bot behavior. It works in a consistant manner but is less powerful.
    //   1: CSS based aim lock: Bots always aim at players' heads. But this may conflict with CS2's native bot logic, causing bots to not react under certain circumstances.
    //   2: Behavior tree based aim lock: Hard mode.
    "BotAimLock": 1,
    // EquipPlayer:
    //   0: Disabled. To avoid interfering with other weapon related plugins
    //   1: Enabled. Players will be equipped with AK47, deagle and nades every time they respawn.
    "EquipPlayer": 1,
    // Soundevent played to the practicing player for each kill. Empty string disables it.
    "HitFeedbackSound": "Buttons.snd9"
}
""";

    /// <summary>
    /// addons/counterstrikesharp/configs/plugins/OpenPrefirePrac/default_cfg.json: outside the plugin
    /// folder, so the settings survive the plugin folder being replaced on an update. Found by walking
    /// up to the counterstrikesharp root, so it also works from plugins/disabled/OpenPrefirePrac.
    /// </summary>
    private string ResolveConfigPath()
    {
        var dir = new DirectoryInfo(Path.GetFullPath(_moduleDirectory));
        while (dir is not null && !string.Equals(dir.Name, "counterstrikesharp", StringComparison.OrdinalIgnoreCase))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            // Unknown layout: keep the config next to the plugin as before.
            return Path.Combine(_moduleDirectory, ConfigFileName);
        }

        return Path.Combine(dir.FullName, "configs", "plugins", "OpenPrefirePrac", ConfigFileName);
    }

    private void EnsureConfigFile(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Older versions read default_cfg.json from the plugin folder: keep those settings.
            var legacyPath = Path.Combine(_moduleDirectory, ConfigFileName);
            if (legacyPath != path && File.Exists(legacyPath))
            {
                File.Copy(legacyPath, path);
                Console.WriteLine($"[OpenPrefirePrac] Copied settings from {legacyPath} to {path}.");
                return;
            }

            File.WriteAllText(path, ConfigTemplate);
            Console.WriteLine($"[OpenPrefirePrac] Created default settings at {path}.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[OpenPrefirePrac] Could not create {path}: {e.Message}. Will use default settings.");
        }
    }

    public void LoadDefaultSettings()
    {
        string path = ResolveConfigPath();
        EnsureConfigFile(path);

        if (!File.Exists(path))
        {
            // Use default settings
            Console.WriteLine("[OpenPrefirePrac] No custom settings provided. Will use default settings.");
        }
        else
        {
            // Load settings from default_cfg.json
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,

            };

            string jsonString = File.ReadAllText(path);
            
            try
            {
                DefaultConfig jsonConfig = JsonSerializer.Deserialize<DefaultConfig>(jsonString, options)!;

                if (jsonConfig.Difficulty > -1 && jsonConfig.Difficulty < 6)
                {
                    Difficulty = jsonConfig.Difficulty;
                }

                if (jsonConfig.TrainingMode > -1 && jsonConfig.TrainingMode < 2)
                {
                    TrainingMode = jsonConfig.TrainingMode;
                }
                
                if (jsonConfig.BotWeapon > -1 && jsonConfig.BotWeapon < 5)
                {
                    BotWeapon = jsonConfig.BotWeapon;
                }

                if (jsonConfig.BotAimLock > -1 && jsonConfig.BotAimLock < 3)
                {
                    BotAimLock = jsonConfig.BotAimLock;
                }

                if (jsonConfig.EquipPlayer > -1 && jsonConfig.EquipPlayer < 2)
                {
                    EquipPlayer = jsonConfig.EquipPlayer;
                }

                if (jsonConfig.HitFeedbackSound != null)
                {
                    HitFeedbackSound = jsonConfig.HitFeedbackSound;
                }

                Console.WriteLine($"[OpenPrefirePrac] Using default settings: Difficulty = {Difficulty}, TrainingMode = {TrainingMode}, BotWeapon = {BotWeapon}, BotAimLock = {BotAimLock}, EquipPlayer = {EquipPlayer}, HitFeedbackSound = {HitFeedbackSound}");
            }
            catch (System.Exception)
            {
                Console.WriteLine("[OpenPrefirePrac] Failed to load custom settings. Will use default settings.");
            }
        }
    }
}