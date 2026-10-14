using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace OpenPrefirePrac;

/// <summary>
/// In-game practice profile recorder: an admin walks a route and builds maps/&lt;map&gt;/&lt;name&gt;.txt with chat
/// commands instead of copying getpos output by hand. Positions are stored in the profile format directly
/// (feet height), so the files must not be run through calculate_height.py.
///
/// !prefire rec start &lt;name&gt;   start a route; your position and view become the player's start
/// !prefire rec bot | crouch     bot where you stand, facing where you look
/// !prefire rec aim | aimc       bot where you aim (crosshair), facing you
/// !prefire rec line             guiding-line point where you stand
/// !prefire rec bots &lt;n&gt; &lt;ratio&gt; bots alive at once and the random-mode ratio (default 6 0.7)
/// !prefire rec incompat &lt;names&gt; practices that share these bots (line 1)
/// !prefire rec undo | info | cancel
/// !prefire rec save [force]     write the file (force overwrites)
/// </summary>
public partial class OpenPrefirePrac
{
    private const string RecordPermission = "@css/root";
    // Guiding-line joints sit 9 units above the feet (eye - 55 in calculate_height.py).
    private const float GuidingLineFeetOffset = PrefirePractice.EyeHeight - PrefirePractice.GuidingLineEyeOffset;
    private const float AimMaxDistance = 4096f;
    // Keep an aimed bot a little off the wall it was aimed at.
    private const float AimWallClearance = 20f;
    private static readonly Regex RecordNamePattern = new("^[a-z0-9_]{1,48}$", RegexOptions.Compiled);

    private sealed class RecordingSession
    {
        public required string Name { get; init; }
        public required string Map { get; init; }
        public required TargetBot Start { get; init; }
        public readonly List<TargetBot> Bots = new();
        public readonly List<Vector> Joints = new();
        // What each add appended, so undo removes the last item of either kind.
        public readonly Stack<bool> AddedBot = new();
        public int NumBots = 6;
        public float SpawnRatio = 0.7f;
        public string Incompatible = "";
    }

    private readonly Dictionary<ulong, RecordingSession> _recordings = new();

    private void HandleRecordCommand(CCSPlayerController player, CommandInfo commandInfo)
    {
        if (!AdminManager.PlayerHasPermissions(player, RecordPermission))
        {
            RecordReply(player, "You need admin access to record practices.");
            return;
        }

        var sub = commandInfo.ArgByIndex(2).ToLowerInvariant();
        _recordings.TryGetValue(player.SteamID, out var session);

        if (sub == "start")
        {
            var name = commandInfo.ArgByIndex(3).ToLowerInvariant();
            if (!RecordNamePattern.IsMatch(name))
            {
                RecordReply(player, "Usage: !prefire rec start <name>  (a-z, 0-9 and _, e.g. a_main)");
                return;
            }

            if (!TryGetFeetAndView(player, out var feet, out var view))
                return;

            _recordings[player.SteamID] = new RecordingSession
            {
                Name = name,
                Map = Server.MapName,
                Start = new TargetBot(feet.X, feet.Y, feet.Z, view.X, view.Y, 0, false),
            };
            RecordReply(player, $"Recording {Server.MapName}/{name}. Your position is the player start. Add bots with bot/crouch/aim, the route with line, then save.");
            return;
        }

        if (session == null)
        {
            RecordReply(player, "No recording. Start one with: !prefire rec start <name>");
            return;
        }

        if (!string.Equals(session.Map, Server.MapName, StringComparison.OrdinalIgnoreCase))
        {
            _recordings.Remove(player.SteamID);
            RecordReply(player, "The map changed, so the recording was discarded.");
            return;
        }

        switch (sub)
        {
            case "bot":
            case "crouch":
            {
                if (!TryGetFeetAndView(player, out var feet, out var view))
                    return;
                AddBot(player, session, new TargetBot(feet.X, feet.Y, feet.Z, view.X, view.Y, 0, sub == "crouch"));
                return;
            }
            case "aim":
            case "aimc":
            {
                if (TryGetAimedBot(player, sub == "aimc", out var bot))
                    AddBot(player, session, bot);
                return;
            }
            case "line":
            {
                if (!TryGetFeetAndView(player, out var feet, out _))
                    return;
                session.Joints.Add(new Vector(feet.X, feet.Y, feet.Z + GuidingLineFeetOffset));
                session.AddedBot.Push(false);
                RecordReply(player, $"Guiding-line point {session.Joints.Count} added.");
                return;
            }
            case "bots":
            {
                if (int.TryParse(commandInfo.ArgByIndex(3), out var n) && n > 0 &&
                    float.TryParse(commandInfo.ArgByIndex(4), NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) && ratio > 0 && ratio <= 1)
                {
                    session.NumBots = n;
                    session.SpawnRatio = ratio;
                    RecordReply(player, $"{n} bots at a time, random-mode ratio {ratio.ToString(CultureInfo.InvariantCulture)}.");
                }
                else
                {
                    RecordReply(player, "Usage: !prefire rec bots <count> <ratio 0-1>, e.g. !prefire rec bots 6 0.7");
                }
                return;
            }
            case "incompat":
            {
                var names = new List<string>();
                for (var i = 3; i < commandInfo.ArgCount; i++)
                {
                    var n = commandInfo.ArgByIndex(i).ToLowerInvariant();
                    if (RecordNamePattern.IsMatch(n)) names.Add(n);
                }
                session.Incompatible = string.Join(" ", names);
                RecordReply(player, names.Count > 0 ? $"Shares bots with: {session.Incompatible}" : "No incompatible practices.");
                return;
            }
            case "undo":
            {
                if (session.AddedBot.Count == 0)
                {
                    RecordReply(player, "Nothing to undo.");
                    return;
                }
                if (session.AddedBot.Pop())
                    session.Bots.RemoveAt(session.Bots.Count - 1);
                else
                    session.Joints.RemoveAt(session.Joints.Count - 1);
                RecordReply(player, $"Removed. {session.Bots.Count} bots, {session.Joints.Count} line points.");
                return;
            }
            case "info":
                RecordReply(player, $"{session.Map}/{session.Name}: {session.Bots.Count} bots, {session.Joints.Count} line points, {session.NumBots} at a time (ratio {session.SpawnRatio.ToString(CultureInfo.InvariantCulture)}).");
                return;
            case "cancel":
                _recordings.Remove(player.SteamID);
                RecordReply(player, "Recording discarded.");
                return;
            case "save":
                SaveRecording(player, session, commandInfo.ArgByIndex(3).Equals("force", StringComparison.OrdinalIgnoreCase));
                return;
            default:
                RecordReply(player, "rec: start <name>, bot, crouch, aim, aimc, line, bots <n> <ratio>, incompat <names>, undo, info, cancel, save [force]");
                return;
        }
    }

    private void AddBot(CCSPlayerController player, RecordingSession session, TargetBot bot)
    {
        session.Bots.Add(bot);
        session.AddedBot.Push(true);
        RecordReply(player, $"Bot {session.Bots.Count} added{(bot.IsCrouching ? " (crouching)" : "")}.");
    }

    private static bool TryGetFeetAndView(CCSPlayerController player, out Vector feet, out QAngle view)
    {
        feet = null!;
        view = null!;
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null || !player.PawnIsAlive)
        {
            RecordReply(player, "You need to be alive to record positions.");
            return false;
        }

        feet = new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z);
        view = new QAngle(pawn.EyeAngles.X, pawn.EyeAngles.Y, 0);
        return true;
    }

    // Places a bot on the floor where the player aims, a little off the hit surface, facing the player.
    private static bool TryGetAimedBot(CCSPlayerController player, bool crouching, out TargetBot bot)
    {
        bot = null!;
        if (!TryGetFeetAndView(player, out var feet, out var view))
            return false;

        var pawn = player.PlayerPawn.Value!;
        var eye = new Vector(feet.X, feet.Y, feet.Z + pawn.ViewOffset.Z);

        double pitch = view.X * Math.PI / 180, yaw = view.Y * Math.PI / 180;
        var dir = new Vector((float)(Math.Cos(pitch) * Math.Cos(yaw)), (float)(Math.Cos(pitch) * Math.Sin(yaw)), (float)-Math.Sin(pitch));
        var worldOnly = new TraceOptions { InteractsWith = Contents.Solid | Contents.Window | Contents.PlayerClip };

        var aimEnd = new Vector(eye.X + dir.X * AimMaxDistance, eye.Y + dir.Y * AimMaxDistance, eye.Z + dir.Z * AimMaxDistance);
        var aim = Trace.TraceEndShape(eye, aimEnd, pawn, worldOnly);
        if (!aim.DidHit())
        {
            RecordReply(player, "Nothing in range where you aim.");
            return false;
        }

        // Step back from the surface along the aim, then drop to the floor below.
        var hit = aim.HitPoint;
        var spot = new Vector(hit.X - dir.X * AimWallClearance, hit.Y - dir.Y * AimWallClearance, hit.Z - dir.Z * AimWallClearance + 32f);
        var down = Trace.TraceEndShape(spot, new Vector(spot.X, spot.Y, spot.Z - 1024f), pawn, worldOnly);
        if (!down.DidHit())
        {
            RecordReply(player, "Could not find the floor at that spot.");
            return false;
        }

        var floor = down.HitPoint;
        var faceYaw = (float)(Math.Atan2(feet.Y - floor.Y, feet.X - floor.X) * 180 / Math.PI);
        bot = new TargetBot(floor.X, floor.Y, floor.Z, 0, faceYaw, 0, crouching);
        return true;
    }

    private void SaveRecording(CCSPlayerController player, RecordingSession session, bool force)
    {
        if (session.Bots.Count == 0)
        {
            RecordReply(player, "Add at least one bot before saving.");
            return;
        }

        var directory = Path.Combine(ModuleDirectory, "maps", session.Map);
        var path = Path.Combine(directory, session.Name + ".txt");
        if (File.Exists(path) && !force)
        {
            RecordReply(player, $"{session.Map}/{session.Name}.txt already exists. Use !prefire rec save force to overwrite it.");
            return;
        }

        static string N(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        static string BotLine(TargetBot b) =>
            $"{N(b.Position.X)} {N(b.Position.Y)} {N(b.Position.Z)} {N(b.Rotation.X)} {N(b.Rotation.Y)} {N(b.Rotation.Z)} {(b.IsCrouching ? "True" : "False")}";

        var sb = new StringBuilder();
        sb.Append(session.Incompatible).Append('\n');
        sb.Append(session.NumBots).Append(' ').Append(N(session.SpawnRatio)).Append('\n');
        var start = session.Start;
        sb.Append($"{N(start.Position.X)} {N(start.Position.Y)} {N(start.Position.Z)} {N(start.Rotation.X)} {N(start.Rotation.Y)} {N(start.Rotation.Z)}\n");
        foreach (var bot in session.Bots)
            sb.Append(BotLine(bot)).Append('\n');
        foreach (var joint in session.Joints)
            sb.Append($"{N(joint.X)} {N(joint.Y)} {N(joint.Z)}\n");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception e)
        {
            RecordReply(player, $"Saving failed: {e.Message}");
            return;
        }

        _recordings.Remove(player.SteamID);

        // Rebuilding the practice list renumbers the practices, so only do it while nobody is practicing.
        if (_playerCount == 0 && string.Equals(session.Map, _mapName, StringComparison.OrdinalIgnoreCase))
        {
            OnMapStartHandler(_mapName);
            RecordReply(player, $"Saved {session.Map}/{session.Name}.txt ({session.Bots.Count} bots). It is in the practice menu now.");
        }
        else
        {
            RecordReply(player, $"Saved {session.Map}/{session.Name}.txt ({session.Bots.Count} bots). It shows up once nobody is practicing and the map reloads.");
        }
    }

    private static void RecordReply(CCSPlayerController player, string message) =>
        player.PrintToChat($" {ChatColors.Green}[OpenPrefirePrac] {ChatColors.White}{message}");
}
