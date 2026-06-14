using System.IO;
using System.Text.Json;

namespace Modinator;

// Optional, local, offline-first override layer for the handful of
// *version-fragile code constants* — NOT struct offsets. Struct field
// offsets are physics and stay in *Native.cs / the OFF_* literals; they
// have held stable across every Steam build (re-confirmed against the
// 2026-05 beta SDK). The thing that actually moves on a DD1 patch is the
// one code-section-dependent address (the Auto-Kill APawn vtable seed)
// plus a couple of tuning heuristics that have drifted before.
//
// Contract:
//   * MaxPlausibleHp / MaxTowerUnits: the compiled-in defaults are
//     authoritative — with no override file those behave exactly as if
//     this class did not exist (offline-first, zero-touch).
//   * PawnVtableSeed: there is NO compiled value (default 0, 2026-06-11).
//     It is always derived from the live game (structural scan /
//     calibration) and pinned here with the game's PE build stamp.
//   * The file at %LOCALAPPDATA%\Modinator\overrides.json (next to
//     hotkeys.json) is a pure patch-day escape hatch: re-point a moved
//     DD1 code address without rebuilding the binary.
//   * Fail-safe by construction — a missing / malformed / partial /
//     out-of-range file falls back per-key to the compiled default,
//     never throws, never half-applies (mirrors the deliberate
//     empty-catch convention in the memory-read paths).
//   * A bad override can never be worse than no override: the Auto-Kill
//     structural fallback (FindWorldInfoViaPawnScan) still rediscovers
//     and rewrites a wrong vtable seed at runtime — the seed only
//     affects the one-time fast path.
internal static class Tunables
{
    // ── Compiled-in defaults (last known-good) ──────────────────────
    // Single source of truth when no override file is present. Keep in
    // sync with the literals these replaced in MainWindow.xaml.cs.
    //
    // The pawn-vtable seed ships EMPTY (0) since 2026-06-10 — it is a
    // derived value, never a shipped one. The first resolve on a fresh
    // install runs the structural scan (WI-loop-closure validated, works
    // even single-pawn in the Tavern), then the verified player pawn's
    // vtable is auto-pinned to overrides.json together with the game's
    // PE build stamp; every later launch fast-paths from the pin, and a
    // stamp mismatch after a DD1 patch drops it for automatic re-derive.
    // Shipped-value history (no longer used; kept in DD1_INTERNALS.md §6):
    // 0x00FCD7D8 → 0x00FCC830 → 0x00FCD870 → 0x00FCE738.
    public const uint DefaultPawnVtableSeed = 0;
    public const int  DefaultMaxPlausibleHp = 500_000_000;
    public const int  DefaultMaxTowerUnits  = 100_000;

    // Accepted ranges for an overridden value. Outside the range → the
    // key is ignored and the default stands (defensive: a typo'd HP cap
    // of 0 would brick the pawn scan; a huge tower count could crash the
    // allocator). The vtable seed has no static range — it is validated
    // structurally at runtime by the pawn scan instead.
    private const long MinPlausibleHp = 1_000_000;
    private const long MaxPlausibleHpCeil = 2_000_000_000;
    private const long MaxTowerUnitsCeil = 1_000_000; // DD_ModMenu's ceiling

    private static readonly object _gate = new();
    private static bool _loaded;
    private static DateTime _pinRetryAfterUtc = DateTime.MinValue; // set after a failed pin write

    private static uint _pawnVtableSeed = DefaultPawnVtableSeed;
    private static int  _maxPlausibleHp = DefaultMaxPlausibleHp;
    private static int  _maxTowerUnits  = DefaultMaxTowerUnits;
    // PE COFF TimeDateStamp of the DunDefGame.exe the pinned seed was
    // derived from (0 = never recorded). Bookkeeping, not an override:
    // lets the Auto-Kill path detect "the game updated since the pin"
    // on attach and skip the doomed fast sweep entirely.
    private static uint _gameStamp;
    private static string _status = "Not loaded yet";

    // Effective values. The getters lazily load the file on first use so
    // field/static initializers (e.g. MainWindow._pawnVtable) resolve to
    // the overridden value without an explicit init ordering dependency.
    public static uint PawnVtableSeed { get { EnsureLoaded(); return _pawnVtableSeed; } }
    public static int  MaxPlausibleHp { get { EnsureLoaded(); return _maxPlausibleHp; } }
    public static int  MaxTowerUnits  { get { EnsureLoaded(); return _maxTowerUnits;  } }

    // Human-readable summary for the Settings panel.
    public static string Status { get { EnsureLoaded(); return _status; } }

    // TimeDateStamp recorded with the last pin (0 = none). See _gameStamp.
    public static uint GameTimeDateStamp { get { EnsureLoaded(); return _gameStamp; } }

    public static string FilePath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Modinator", "overrides.json");

    // Idempotent; safe from any thread / any number of times.
    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate) { if (_loaded) return; Apply(); _loaded = true; }
    }

    // Force a re-read (Settings "Reload" button). Re-applies from
    // defaults so removing a key / deleting the file also takes effect.
    public static void Reload()
    {
        lock (_gate) { Apply(); _loaded = true; }
    }

    // Mirrors the on-disk JSON. Nullable everywhere → an absent key keeps
    // the compiled default. Hex ("0x00FCD7D8") or decimal both accepted
    // for the vtable seed.
    private sealed class OverrideFile
    {
        public string? PawnVtableSeed    { get; set; }
        public long?   MaxPlausibleHp    { get; set; }
        public long?   MaxTowerUnits     { get; set; }
        public string? GameTimeDateStamp { get; set; }
        public string? Note              { get; set; }
    }

    private static void Apply()
    {
        // Always restart from defaults so Reload() can also REMOVE an
        // override, and a partial file only overrides the keys it sets.
        uint vtable = DefaultPawnVtableSeed;
        int  maxHp  = DefaultMaxPlausibleHp;
        int  maxTu  = DefaultMaxTowerUnits;
        uint stamp  = 0;
        var applied = new List<string>();
        string outcome;

        try
        {
            if (!File.Exists(FilePath))
            {
                outcome = "No saved file yet — addresses are learned automatically on the first scan";
            }
            else
            {
                var f = JsonSerializer.Deserialize<OverrideFile>(
                    File.ReadAllText(FilePath),
                    new JsonSerializerOptions
                    {
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });

                if (f == null)
                {
                    outcome = "Saved file couldn't be read — it will be rebuilt automatically on the next scan";
                }
                else
                {
                    if (TryParseU32(f.PawnVtableSeed, out uint v) && v != 0)
                    {
                        vtable = v;
                        applied.Add($"PawnVtableSeed=0x{v:X8}");
                    }

                    if (f.MaxPlausibleHp is long hp &&
                        hp >= MinPlausibleHp && hp <= MaxPlausibleHpCeil)
                    {
                        maxHp = (int)hp;
                        applied.Add($"MaxPlausibleHp={hp:N0}");
                    }

                    if (f.MaxTowerUnits is long tu &&
                        tu > 0 && tu <= MaxTowerUnitsCeil)
                    {
                        maxTu = (int)tu;
                        applied.Add($"MaxTowerUnits={tu:N0}");
                    }

                    // Bookkeeping only — deliberately NOT in `applied`
                    // (it is metadata about the pin, not an override).
                    TryParseU32(f.GameTimeDateStamp, out stamp);

                    outcome = applied.Count == 0
                        ? "Saved file has no usable values — using defaults"
                        : "Using saved values — " + string.Join(", ", applied);
                }
            }
        }
        catch
        {
            // Any error → the full compiled default set. Never worse
            // than no file.
            vtable = DefaultPawnVtableSeed;
            maxHp  = DefaultMaxPlausibleHp;
            maxTu  = DefaultMaxTowerUnits;
            stamp  = 0;
            outcome = "Saved file couldn't be read — it will be rebuilt automatically on the next scan";
        }

        _pawnVtableSeed = vtable;
        _maxPlausibleHp = maxHp;
        _maxTowerUnits  = maxTu;
        _gameStamp      = stamp;
        _status = outcome;
        Base.Log($"Tunables: {outcome} (file: {FilePath})");
    }

    private static bool TryParseU32(string? s, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        try
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = Convert.ToUInt32(s.Substring(2), 16);
            else if (uint.TryParse(s, out uint d))
                value = d;
            else
                return false;
            return true;
        }
        catch { return false; }
    }

    // Writes a template populated from the CURRENT EFFECTIVE values, so
    // the file is never hand-authored from a generic example. Returns
    // the written path, or null on failure.
    public static string? WriteTemplate()
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var tpl = new OverrideFile
            {
                Note = "GrandeuReforged memory overrides. Remove a key (or delete this " +
                       "file) to use the compiled-in default. PawnVtableSeed is the " +
                       "DunDefGame APawn-vtable RVA in hex; the Auto-Kill structural " +
                       "fallback self-heals a wrong value at runtime, so this is only a " +
                       "fast-path optimization. Struct offsets are NOT overridable here " +
                       "by design — they are stable.",
                PawnVtableSeed = $"0x{PawnVtableSeed:X8}",
                MaxPlausibleHp = MaxPlausibleHp,
                MaxTowerUnits  = MaxTowerUnits,
                // Preserve the recorded build stamp — losing it would just
                // cost one wasted fast sweep after the next patch, but
                // there's no reason for a template write to discard it.
                GameTimeDateStamp = GameTimeDateStamp != 0 ? $"0x{GameTimeDateStamp:X8}" : null,
            };
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(tpl, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                }));
            return FilePath;
        }
        catch { return null; }
    }

    // Auto-pin: called by the Auto-Kill structural self-heal the moment
    // it rediscovers the live APawn vtable (the ONLY value that moves on
    // a DD1 patch). Persists it into overrides.json so every subsequent
    // launch takes the fast path with zero rediscovery and zero user
    // action — this is the "automatic updater": the tool teaches itself
    // the new code address from live memory (not a guess, not the SDK
    // dump) and writes it down. Other keys already in the file are
    // preserved. Fail-safe + atomic; never throws into the AK loop; only
    // writes when the value actually changed (≈ once per patch).
    public static void PinPawnVtable(uint discovered, uint liveStamp = 0)
    {
        if (discovered == 0) return;
        lock (_gate)
        {
            EnsureLoadedLocked();
            bool seedChanged  = discovered != _pawnVtableSeed;
            // Stamp-only writes happen once per game build (e.g. the very
            // first pin baselines the stamp so patch detection can work
            // from then on); a 0 liveStamp means "PE header unreadable —
            // don't touch the recorded value".
            bool stampChanged = liveStamp != 0 && liveStamp != _gameStamp;
            // No-churn check: skip only when the values are already effective
            // AND the file actually exists on disk. The in-memory compare
            // alone is not enough — rename/delete overrides.json mid-session
            // (the cold-start test does exactly that) and every later pin
            // would silently no-op against stale memory, so the file never
            // reappears while the UI reports "saved" (user-hit 2026-06-11).
            // File.Exists is an OS-cached metadata stat — cheap enough for
            // the per-tick caller.
            if (!seedChanged && !stampChanged && File.Exists(FilePath)) return;
            // Backoff after a failed write: PinPawnVtable is called every AK
            // tick (100 ms), and a persistently unwritable folder (read-only,
            // AV lock) would otherwise retry the full serialize+write 10x per
            // second forever, silently.
            if (DateTime.UtcNow < _pinRetryAfterUtc) return;
            try
            {
                OverrideFile f = TryReadFileLocked() ?? new OverrideFile
                {
                    MaxPlausibleHp = _maxPlausibleHp,
                    MaxTowerUnits  = _maxTowerUnits,
                };
                f.PawnVtableSeed = $"0x{discovered:X8}";
                if (liveStamp != 0)
                    f.GameTimeDateStamp = $"0x{liveStamp:X8}";
                f.Note = "Auto-pinned by GrandeuReforged: the Auto-Kill structural " +
                         "self-heal rediscovered the live DunDefGame APawn-vtable RVA " +
                         "after a game patch. GameTimeDateStamp is the exe build the " +
                         "seed belongs to (used to skip the stale fast scan after the " +
                         "next patch). Delete this file (or a key) to fall back to the " +
                         "compiled default + runtime re-discovery.";

                string dir = System.IO.Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp,
                    JsonSerializer.Serialize(f, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    }));
                File.Move(tmp, FilePath, overwrite: true);

                _pawnVtableSeed = discovered;
                if (liveStamp != 0) _gameStamp = liveStamp;
                if (seedChanged)
                    _status = $"Address learned and saved — PawnVtableSeed=0x{discovered:X8}";
                Base.Log($"Tunables: pinned seed=0x{discovered:X8} stamp=0x{liveStamp:X8} (seedChanged={seedChanged})");
            }
            catch
            {
                // Persisting is best-effort. The in-memory _pawnVtable in
                // MainWindow is already corrected for this session, so a
                // write failure only costs one rediscovery next launch.
                _pinRetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
            }
        }
    }

    // Lock-held variants used by PinPawnVtable (the public EnsureLoaded
    // also takes _gate, which would re-enter — these assume it is held).
    private static void EnsureLoadedLocked()
    {
        if (_loaded) return;
        Apply();
        _loaded = true;
    }

    private static OverrideFile? TryReadFileLocked()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<OverrideFile>(
                File.ReadAllText(FilePath),
                new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
        }
        catch { return null; }
    }
}
