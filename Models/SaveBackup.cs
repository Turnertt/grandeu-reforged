using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Modinator;

// DD1 save-file backup + restore. Pure file I/O — no game memory involved.
//
// What is backed up: every file in the game's Steam Cloud "remote" folder
// (Steam\userdata\<account>\65800\remote — DunDefHeroes.dun plus its
// .cbb / .rdx siblings), copied to a dated folder under
// %LOCALAPPDATA%\Modinator\save-backups\ with a small manifest.
//
// When: (1) at app startup, (2) right before the FIRST game write of the
// session (hooked in Scanner.WriteMemory — every item/hero/misc edit goes
// through it), (3) BACKUP NOW in Settings, (4) automatically before a
// restore ("pre-restore"). (1) and (2) are de-duplicated against the newest
// backup by the .dun's size + write time, so launching the tool ten times
// without the game saving in between makes one backup, not ten.
//
// Why "before the first write" is the right moment: DD1 only persists the
// in-memory state when IT saves (leaving the tavern, box changes, ...). The
// on-disk file at the first tool write is therefore the last state the game
// wrote before any of this session's edits could be persisted.
//
// Restore: refuses while DunDefGame.exe is running (the game would simply
// overwrite the file again on its next save), takes a pre-restore backup of
// what is there now, then copies the chosen backup's files back.
internal sealed class BackupInfo
{
    public string Folder = "";
    public DateTime CreatedUtc;
    public string Kind = "auto";       // auto | manual | pre-restore
    public long Bytes;
    public long SaveLength;            // DunDefHeroes.dun size at backup time
    public DateTime SaveWriteTimeUtc;  // DunDefHeroes.dun write time at backup time
    public int FileCount;

    public DateTime CreatedLocal => CreatedUtc.ToLocalTime();
    public string KindLabel => Kind switch
    {
        "manual" => "Manual",
        "pre-restore" => "Before restore",
        _ => "Automatic",
    };
}

internal static class SaveBackup
{
    public const string SaveFileName = "DunDefHeroes.dun";
    private const string ManifestName = "grandeu-backup.json";
    // Staging suffix for a backup still being written (see CreateBackup).
    private const string PartialSuffix = ".partial";
    // Automatic backups are pure churn — one at startup and one before the
    // first edit of every session — so they are the ones that pile up and the
    // ones worth capping. Manual and pre-restore backups are deliberate acts
    // (a snapshot the user asked for; the undo for a restore) and are never
    // pruned, so this is a cap on accumulation rather than on total count.
    private const int AutoBackupsKept = 10;
    private const long MaxFileBytes = 64L * 1024 * 1024; // skip anything absurd

    public static string BackupRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Modinator", "save-backups");

    private static readonly object _gate = new();
    private static volatile bool _sessionBackupDone;
    private static DateTime _nextWriteAttemptUtc = DateTime.MinValue;

    // Last outcome, for the Settings card. Never throws to callers.
    public static string? LastError { get; private set; }

    // True once this session's pre-edit snapshot has actually SUCCEEDED.
    // The UI promises a backup before the first edit, so the Settings card
    // and the diagnostic report surface this rather than assuming it.
    public static bool SessionBackupDone => _sessionBackupDone;

    // ── Save-folder discovery ───────────────────────────────────────

    // Returns the folder containing DunDefHeroes.dun, or null. `how` says
    // which rule found it (shown in Settings so the user can sanity-check).
    public static string? ResolveSaveFolder(out string how)
    {
        how = "";
        try
        {
            string? ov = Prefs.Current.SaveFolderOverride;
            if (!string.IsNullOrWhiteSpace(ov))
            {
                if (File.Exists(Path.Combine(ov, SaveFileName))) { how = "set in Settings"; return ov; }
                how = "the folder set in Settings has no " + SaveFileName + " — using auto-detect";
            }

            string? best = null;
            DateTime bestWrite = DateTime.MinValue;
            foreach (string root in CandidateSteamRoots())
            {
                string userdata = Path.Combine(root, "userdata");
                if (!Directory.Exists(userdata)) continue;
                foreach (string acct in Directory.GetDirectories(userdata))
                {
                    string remote = Path.Combine(acct, "65800", "remote");
                    string dun = Path.Combine(remote, SaveFileName);
                    if (!File.Exists(dun)) continue;
                    DateTime w = File.GetLastWriteTimeUtc(dun);
                    // Several Steam accounts → the one whose save was written most recently.
                    if (best == null || w > bestWrite) { best = remote; bestWrite = w; }
                }
            }
            if (best != null)
            {
                how = string.IsNullOrEmpty(how) ? "auto-detected from Steam" : how;
                return best;
            }
        }
        catch { }
        how = "not found — set the folder in Settings → Save Backups";
        return null;
    }

    private static IEnumerable<string> CandidateSteamRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? raw in RawSteamRoots())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string p = NormalizeRoot(raw);
            if (p.Length == 0 || !seen.Add(p)) continue;
            if (Directory.Exists(p)) yield return p;
        }
    }

    private static IEnumerable<string?> RawSteamRoots()
    {
        // 1. Steam client's own registry value (Windows; Proton prefixes
        //    usually carry it too, sometimes as a Z:\ Linux path).
        yield return ReadReg(Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        yield return ReadReg(Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        yield return ReadReg(Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        // 2. Proton exports the Linux client path into the game's (and so
        //    our) environment when launched inside the prefix.
        yield return Environment.GetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH");
        // 3. Plain defaults.
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam");
    }

    private static string? ReadReg(Microsoft.Win32.RegistryKey hive, string sub, string name)
    {
        try
        {
            using var k = hive.OpenSubKey(sub);
            return k?.GetValue(name) as string;
        }
        catch { return null; }
    }

    // Forward slashes → backslashes; a bare Linux path (Proton) → Wine's
    // default Z: drive mapping of the host root.
    private static string NormalizeRoot(string raw)
    {
        string p = raw.Trim();
        if (p.StartsWith("/")) p = "Z:" + p;
        return p.Replace('/', '\\').TrimEnd('\\');
    }

    // ── Backup ──────────────────────────────────────────────────────

    // Called by Scanner.WriteMemory before the first game write of the
    // session. Cheap after the first SUCCESS (one bool). Never throws.
    //
    // The done-flag is set only when a backup actually succeeded (or the
    // dedup path proved an identical one already exists). Setting it up
    // front — as this first did — meant one failed attempt (save folder not
    // found yet, file briefly locked) silently disabled the promised
    // pre-edit snapshot for the entire session.
    //
    // Retries are THROTTLED because this runs on every single WriteMemory:
    // a bulk edit issues thousands, and each unthrottled attempt would cost
    // a registry read, a directory walk and a JSON parse per existing
    // backup. 30 s between attempts keeps a persistent failure (read-only
    // folder, no Steam install) from turning every write into disk I/O.
    public static void OnGameWrite()
    {
        if (_sessionBackupDone) return;
        if (DateTime.UtcNow < _nextWriteAttemptUtc) return;
        _nextWriteAttemptUtc = DateTime.UtcNow.AddSeconds(30);
        try
        {
            if (CreateBackup("auto", skipIfUnchanged: true) != null)
                _sessionBackupDone = true;
        }
        catch { }
    }

    // Called once at app startup (off the UI thread). Never throws.
    public static void OnStartup()
    {
        try
        {
            if (CreateBackup("auto", skipIfUnchanged: true) != null)
                _sessionBackupDone = true;
        }
        catch { }
    }

    // Copies the save folder into a new dated backup. Returns the backup
    // (or, with skipIfUnchanged, the existing newest one when the save is
    // byte-identical by size + write time), or null on failure (LastError).
    public static BackupInfo? CreateBackup(string kind, bool skipIfUnchanged)
    {
        lock (_gate)
        {
            LastError = null;
            string? src = ResolveSaveFolder(out string how);
            if (src == null) { LastError = "Save folder " + how; return null; }

            var dun = new FileInfo(Path.Combine(src, SaveFileName));
            if (skipIfUnchanged)
            {
                BackupInfo? newest = ListBackups().FirstOrDefault();
                if (newest != null && newest.SaveLength == dun.Length &&
                    newest.SaveWriteTimeUtc == dun.LastWriteTimeUtc)
                    return newest;
            }

            // Build into a ".partial" directory and rename only once every
            // file AND the manifest are on disk. Copying straight into the
            // final folder meant a failure part-way (a locked .cbb, disk
            // full) left a directory holding just DunDefHeroes.dun — which
            // ReadBackup happily listed as a restorable backup, and which
            // also satisfied the dedup check below, suppressing every later
            // automatic backup until the save changed again.
            string dest = "", staging = "";
            try
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                dest = Path.Combine(BackupRoot, stamp + "_" + kind);
                for (int n = 2; Directory.Exists(dest); n++)
                    dest = Path.Combine(BackupRoot, stamp + "_" + kind + "-" + n);
                staging = dest + PartialSuffix;
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                Directory.CreateDirectory(staging);

                long bytes = 0; int count = 0;
                foreach (string f in Directory.GetFiles(src))
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > MaxFileBytes) continue;
                    File.Copy(f, Path.Combine(staging, fi.Name), overwrite: true);
                    bytes += fi.Length; count++;
                }

                var info = new BackupInfo
                {
                    Folder = dest,
                    CreatedUtc = DateTime.UtcNow,
                    Kind = kind,
                    Bytes = bytes,
                    FileCount = count,
                    SaveLength = dun.Exists ? dun.Length : 0,
                    SaveWriteTimeUtc = dun.Exists ? dun.LastWriteTimeUtc : DateTime.MinValue,
                };
                File.WriteAllText(Path.Combine(staging, ManifestName),
                    JsonSerializer.Serialize(new Manifest
                    {
                        Kind = kind,
                        CreatedUtc = info.CreatedUtc,
                        SourceFolder = src,
                        SaveLength = info.SaveLength,
                        SaveWriteTimeUtc = info.SaveWriteTimeUtc,
                    }, new JsonSerializerOptions { WriteIndented = true }));

                // Commit. A backup becomes visible to ListBackups/restore
                // only at this instant.
                Directory.Move(staging, dest);

                PruneAutomatic();
                return info;
            }
            catch (Exception ex)
            {
                LastError = "Backup failed: " + ex.Message;
                try { if (staging.Length > 0 && Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
                catch { }
                return null;
            }
        }
    }

    // Keep the newest AutoBackupsKept automatic backups; manual and
    // pre-restore backups are never pruned.
    private static void PruneAutomatic()
    {
        try
        {
            var autos = ListBackups().Where(b => b.Kind == "auto").Skip(AutoBackupsKept).ToList();
            foreach (var b in autos)
                try { Directory.Delete(b.Folder, recursive: true); } catch { }
        }
        catch { }
    }

    // Newest first.
    public static List<BackupInfo> ListBackups()
    {
        var list = new List<BackupInfo>();
        try
        {
            if (!Directory.Exists(BackupRoot)) return list;
            foreach (string dir in Directory.GetDirectories(BackupRoot))
            {
                // Staging leftovers (process killed mid-copy) are never
                // backups — sweep them instead of listing them.
                if (dir.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                    continue;
                }
                var b = ReadBackup(dir);
                if (b != null) list.Add(b);
            }
        }
        catch { }
        list.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        return list;
    }

    private static BackupInfo? ReadBackup(string dir)
    {
        try
        {
            string dun = Path.Combine(dir, SaveFileName);
            if (!File.Exists(dun)) return null; // not one of ours / incomplete
            // The manifest is the completion marker: it is written last and
            // the folder is only renamed into place afterwards, so a folder
            // without one is not a finished backup and must not be offered
            // for restore. (Deriving the fields from the copied file instead
            // — as this used to — is what let a half-copied backup look real
            // AND satisfy the dedup check that suppresses later backups.)
            string man = Path.Combine(dir, ManifestName);
            if (!File.Exists(man)) return null;
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(man));
            if (m == null) return null;
            var info = new BackupInfo
            {
                Folder = dir,
                CreatedUtc = m.CreatedUtc,
                Kind = m.Kind ?? "auto",
                SaveLength = m.SaveLength,
                SaveWriteTimeUtc = m.SaveWriteTimeUtc,
            };
            foreach (string f in Directory.GetFiles(dir))
            {
                if (Path.GetFileName(f).Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
                info.Bytes += new FileInfo(f).Length;
                info.FileCount++;
            }
            return info;
        }
        catch { return null; }
    }

    // ── Restore ─────────────────────────────────────────────────────

    // Fail CLOSED: this gates a destructive restore, so "couldn't tell"
    // must mean "assume it's running" and block. Returning false on an
    // exception let a process-enumeration failure authorize overwriting the
    // save while the game held it open.
    public static bool GameRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName("DunDefGame");
            bool running = procs.Length > 0;
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
            return running;
        }
        catch { return true; }
    }

    // Returns null on success, else a user-facing reason.
    public static string? Restore(BackupInfo backup)
    {
        lock (_gate)
        {
            if (GameRunning())
                return "Close Dungeon Defenders first. While the game is running it keeps its own copy " +
                       "of the save in memory and would overwrite the restored file the next time it saves.";
            string? dst = ResolveSaveFolder(out string how);
            if (dst == null) return "Save folder " + how + ".";
            if (!Directory.Exists(backup.Folder)) return "That backup folder no longer exists.";

            // Safety net: whatever is there now becomes a backup too.
            if (CreateBackup("pre-restore", skipIfUnchanged: false) == null)
                return LastError ?? "Couldn't back up the current save before restoring — nothing was changed.";

            try
            {
                var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string f in Directory.GetFiles(backup.Folder))
                {
                    string name = Path.GetFileName(f);
                    if (name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
                    File.Copy(f, Path.Combine(dst, name), overwrite: true);
                    restored.Add(name);
                }
                // A restore should reproduce the snapshot, not overlay it.
                // Remove save files that exist now but were NOT in the chosen
                // backup, so the .dun can't be left beside a companion file
                // from a different generation. Scoped to the save's own file
                // family so nothing unrelated in the folder is touched, and
                // recoverable either way from the pre-restore backup taken
                // above.
                if (restored.Contains(SaveFileName))
                {
                    foreach (string f in Directory.GetFiles(dst, SaveFileName + "*"))
                    {
                        string name = Path.GetFileName(f);
                        if (restored.Contains(name)) continue;
                        try { File.Delete(f); } catch { }
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                return "Restore failed part-way: " + ex.Message +
                       "\nA 'Before restore' backup of the previous files was taken first — restore that to undo.";
            }
        }
    }

    private sealed class Manifest
    {
        public string? Kind { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string? SourceFolder { get; set; }
        public long SaveLength { get; set; }
        public DateTime SaveWriteTimeUtc { get; set; }
    }
}
