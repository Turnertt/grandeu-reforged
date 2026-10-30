using System.IO;
using System.Text.Json;

namespace Modinator;

// Small persisted preferences file: %LOCALAPPDATA%\Modinator\prefs.json,
// beside hotkeys.json and overrides.json. Deliberately NOT beside the exe
// (the earlier AppPrefs removal objected to files written into the exe
// folder, not to preferences as such — see DECISIONS.md backlog entry).
// Fail-safe: a missing / unreadable file is the defaults; Save never throws.
public sealed class Prefs
{
    // Bump when the disclaimer text changes materially so users see it again.
    public const int CurrentDisclaimerVersion = 1;

    public int DisclaimerAcceptedVersion { get; set; }

    // Manual DD1 save-folder override (Settings → Save Backups → CHANGE).
    // null/empty = auto-detect via Steam userdata.
    public string? SaveFolderOverride { get; set; }

    // Append "[Grandeu Reforged]" to the Description of items this
    // tool edits (see Models/Watermark.cs). Escape hatch only — no UI, on by
    // default; a prefs.json written before this existed keeps the default.
    public bool WatermarkEditedItems { get; set; } = true;

    // Write a session log to %LOCALAPPDATA%\Modinator\modinator_log.txt so it
    // can be shared when something goes wrong. ON by default: a log that only
    // exists after the user has already hit the bug is no use. One session per
    // file — Base.BeginSession moves the previous one to modinator_log.prev.txt.
    public bool ErrorLogEnabled { get; set; } = true;

    // The DunDefGame.exe build (PE stamp + date) seen on the last attach, so
    // the log can say "same build as last time" / "NEW GAME BUILD". Read from
    // the exe file, never game memory. Bookkeeping only — nothing keys off it.
    public string? LastGameBuild { get; set; }

    public static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Modinator", "prefs.json");

    private static readonly object _gate = new();
    private static Prefs? _current;

    public static Prefs Current
    {
        get
        {
            lock (_gate)
            {
                if (_current != null) return _current;
                try
                {
                    if (File.Exists(FilePath))
                        _current = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(FilePath),
                            new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                }
                catch { _current = null; }
                return _current ??= new Prefs();
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }
    }
}
