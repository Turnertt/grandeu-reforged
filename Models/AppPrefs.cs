using System;
using System.Globalization;
using System.IO;

namespace Modinator;

// Tiny file-backed preference store for values that should survive a
// process restart. Intentionally minimal (one float for now) — using the
// same AppContext.BaseDirectory as the log so there's no AppData setup
// ceremony. Grow into a real config class if we ever need more than a
// handful of scalars.
internal static class AppPrefs
{
    private static readonly string PrefsPath = Path.Combine(
        AppContext.BaseDirectory, "prefs.txt");

    public static float SpeedMultiplier { get; set; } = 1.0f;

    public static void Load()
    {
        try
        {
            if (!File.Exists(PrefsPath)) return;
            foreach (string line in File.ReadAllLines(PrefsPath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (key == "speed" && float.TryParse(val,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    SpeedMultiplier = Math.Clamp(f, 0.05f, 15.0f);
            }
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllText(PrefsPath,
                $"speed={SpeedMultiplier.ToString("0.###", CultureInfo.InvariantCulture)}\n");
        }
        catch { }
    }
}
