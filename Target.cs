using System.Globalization;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace OpenPrefirePrac;
public class TargetBot
{
    public readonly Vector Position;
    public readonly QAngle Rotation;
    public readonly bool IsCrouching;

    public TargetBot(float pX, float pY, float pZ, float aX, float aY, float aZ, bool isCrouching)
    {
        Position = new Vector(pX, pY, pZ);
        Rotation = new QAngle(aX, aY, aZ);
        IsCrouching = isCrouching;
    }
}

public class PrefirePractice
{
    public readonly List<TargetBot> Targets;
    public readonly TargetBot Player;
    public readonly string PracticeName;
    public readonly List<string> IncompatiblePractices;
    public readonly int NumBots;
    public readonly float SpawnRatio;
    public readonly List<Vector> GuidingLine;
    private static readonly char[] Delimiters = { ' ', ',', '\t' };

    // getpos prints the eye position. Bots and the player are placed by their feet, 64 units lower when
    // standing; guiding-line joints sit 9 units above the feet (eye - 55). Same numbers calculate_height.py uses.
    public const float EyeHeight = 64f;
    public const float GuidingLineEyeOffset = 55f;

    private static float F(string value) => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a line pasted straight from the in-game getpos command:
    /// "setpos X Y Z;setang P Y R [True|False] [comment]" is a bot (or the player on line 3),
    /// "setpos X Y Z" alone is a guiding-line joint. Heights are converted like calculate_height.py does.
    /// </summary>
    private static bool TryParseGetPos(string line, out TargetBot? bot, out Vector? joint)
    {
        bot = null;
        joint = null;

        var trimmed = line.Trim();
        if (!trimmed.StartsWith("setpos", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = trimmed.Substring("setpos".Length).Split(';', 2);
        var pos = parts[0].Split(Delimiters, StringSplitOptions.RemoveEmptyEntries);
        if (pos.Length < 3)
            throw new FormatException($"setpos needs 3 numbers: \"{line}\"");

        float x = F(pos[0]), y = F(pos[1]), z = F(pos[2]);

        if (parts.Length < 2 || !parts[1].TrimStart().StartsWith("setang", StringComparison.OrdinalIgnoreCase))
        {
            joint = new Vector(x, y, z - GuidingLineEyeOffset);
            return true;
        }

        var ang = parts[1].TrimStart().Substring("setang".Length).Split(Delimiters, StringSplitOptions.RemoveEmptyEntries);
        if (ang.Length < 3)
            throw new FormatException($"setang needs 3 numbers: \"{line}\"");

        // Anything after the angles that isn't True/False is a comment, as in calculate_height.py.
        bool crouching = ang.Length > 3 && bool.TryParse(ang[3], out var c) && c;
        bot = new TargetBot(x, y, z - EyeHeight, F(ang[0]), F(ang[1]), F(ang[2]), crouching);
        return true;
    }

    public PrefirePractice(string moduleDirectory, string map, string practice)
    {
        Targets = new List<TargetBot>();
        IncompatiblePractices = new List<string>();
        GuidingLine = new List<Vector>();

        // Construct a practice from the description file.
        PracticeName = practice;
        var path = $"{moduleDirectory}/maps/{map}/{practice}.txt";
        // Console.WriteLine("[OpenPrefirePrac] Reading practice file: " + path);
        
        using var sr = File.OpenText(path);

        // The first line contains incompatible practices.
        var s = sr.ReadLine();
        if (s == null)
        {
            throw new Exception($"[OpenPrefirePrac] Reading practice file error (1st line): {path}");
        }
            
        var w = s.Split(Delimiters);
        for (var i = 0; i < w.Length; i++)
        {
            IncompatiblePractices.Add(w[i]);
        }

        // The second line indicates how many bots are needed, and the spawn ratio.
        s = sr.ReadLine();
        if (s == null)
        {
            throw new Exception($"[OpenPrefirePrac] Reading practice file error (2nd line): {path}");
        }
            
        w = s.Split(Delimiters);
        NumBots = Convert.ToInt32(w[0]);
        SpawnRatio = F(w[1]);

        // The third line contains player's position and rotation.
        s = sr.ReadLine();
        if (s == null)
        {
            throw new Exception($"[OpenPrefirePrac] Reading practice file error (3rd line): {path}");
        }
            
        if (TryParseGetPos(s, out var getPosPlayer, out _) && getPosPlayer != null)
        {
            Player = new TargetBot(getPosPlayer.Position.X, getPosPlayer.Position.Y, getPosPlayer.Position.Z,
                getPosPlayer.Rotation.X, getPosPlayer.Rotation.Y, getPosPlayer.Rotation.Z, false);
        }
        else
        {
            w = s.Split(Delimiters);
            Player = new TargetBot(F(w[0]), F(w[1]), F(w[2]), F(w[3]), F(w[4]), F(w[5]), false);
        }

        while ((s = sr.ReadLine()) != null)
        {
            // Lines pasted from getpos ("setpos ...;setang ...") work as-is, heights converted here.
            if (TryParseGetPos(s, out var getPosBot, out var getPosJoint))
            {
                if (getPosBot != null) Targets.Add(getPosBot);
                if (getPosJoint != null) GuidingLine.Add(getPosJoint);
                continue;
            }

            w = s.Split(Delimiters);
            
            // A line with 7 segments defines a target bot. Comments will be ignored.
            if (w.Length >= 7)
                Targets.Add(new TargetBot(F(w[0]), F(w[1]), F(w[2]), F(w[3]), F(w[4]), F(w[5]), Convert.ToBoolean(w[6])));
            
            // A line with 3 real numbers defines a joint point of the guiding line.
            if (w.Length == 3)
                GuidingLine.Add(new Vector(F(w[0]), F(w[1]), F(w[2])));
        }
    }
}
