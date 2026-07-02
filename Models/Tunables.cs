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
    // int.MaxValue = the HP plausibility cap is effectively OFF by default
    // (2026-07-02). The 500M default it replaces silently broke Auto-Kill
    // on high-HP content at three gates: the structural scan pre-filter
    // rejected >cap pawns, ValidateCachedWorldInfo evicted the WorldInfo
    // cache every tick while a >cap enemy sat at the pawn-list head
    // (newest spawn), and the tail hero gate refused to learn. The scan's
    // real noise rejection is the vtable/backref/loop-closure chain, not
    // this cap; the override key remains for hand-tuning if ever needed.
    public const int  DefaultMaxPlausibleHp = int.MaxValue;
    public const int  DefaultMaxTowerUnits  = 100_000;
    // Forge-box (ItemBoxEquipments) offset off the HeroManager. Unlike the
    // other struct offsets (which are physics and live in code), this one
    // has moved on a DD1 patch (0x39C → 0x3A8, 2026-06), so it is treated
    // like the vtable seed: compiled last-known-good default, discovered +
    // pinned from live memory on a patch. Keep in sync with
    // GameChain.OFF_HM_ITEMBOX.
    public const int  DefaultItemBoxOffset  = 0x39C;
    // The two other game-class links on the same chain, promoted to
    // discovered + pinned defaults for the same reason (same insertion
    // mechanism that moved the box): the LocalLoadedHeroes/ActiveHeroes
    // pair base off the HeroManager (ActiveHeroes is always +0xC above),
    // and the TheHeroManager hop off the ViewportClient. Keep in sync
    // with GameChain.OFF_HM_LOCALHEROES / OFF_VIEWPORT_HEROMGR.
    public const int  DefaultLocalHeroesOffset = 0x360;
    public const int  DefaultHeroManagerOffset = 0xCFC;

    // Accepted ranges for an overridden value. Outside the range → the
    // key is ignored and the default stands (defensive: a typo'd HP cap
    // of 0 would brick the pawn scan; a huge tower count could crash the
    // allocator). The vtable seed has no static range — it is validated
    // structurally at runtime by the pawn scan instead.
    private const long MinPlausibleHp = 1_000_000;
    private const long MaxPlausibleHpCeil = int.MaxValue;
    private const long MaxTowerUnitsCeil = 1_000_000; // DD_ModMenu's ceiling
    // The discovered offsets are small, 4-aligned struct offsets. A value
    // outside its band (or misaligned) is a typo/garbage and is ignored,
    // leaving the discovered-or-default value to stand. The HeroManager
    // hop lives much deeper into its class (0xCFC), hence the wider band.
    private const int  MinItemBoxOffset = 0x100;
    private const int  MaxItemBoxOffset = 0x800;
    private const int  MinLocalHeroesOffset = 0x100;
    private const int  MaxLocalHeroesOffset = 0x800;
    private const int  MinHeroManagerOffset = 0x400;
    private const int  MaxHeroManagerOffset = 0x3000;

    private static readonly object _gate = new();
    private static bool _loaded;
    private static DateTime _pinRetryAfterUtc = DateTime.MinValue; // set after a failed pin write

    private static uint _pawnVtableSeed = DefaultPawnVtableSeed;
    private static int  _maxPlausibleHp = DefaultMaxPlausibleHp;
    private static int  _maxTowerUnits  = DefaultMaxTowerUnits;
    private static int  _itemBoxOffset  = DefaultItemBoxOffset;
    private static int  _localHeroesOffset = DefaultLocalHeroesOffset;
    private static int  _heroManagerOffset = DefaultHeroManagerOffset;
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
    public static int  ItemBoxOffset  { get { EnsureLoaded(); return _itemBoxOffset;  } }
    public static int  LocalHeroesOffset { get { EnsureLoaded(); return _localHeroesOffset; } }
    public static int  HeroManagerOffset { get { EnsureLoaded(); return _heroManagerOffset; } }

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
        public string? ItemBoxOffset     { get; set; }
        public string? LocalHeroesOffset { get; set; }
        public string? HeroManagerOffset { get; set; }
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
        int  itemBox = DefaultItemBoxOffset;
        int  localHeroes = DefaultLocalHeroesOffset;
        int  heroMgrOff  = DefaultHeroManagerOffset;
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

                    if (TryParseU32(f.ItemBoxOffset, out uint ib) &&
                        IsValidItemBoxOffset((int)ib))
                    {
                        itemBox = (int)ib;
                        applied.Add($"ItemBoxOffset=0x{itemBox:X}");
                    }

                    if (TryParseU32(f.LocalHeroesOffset, out uint lh) &&
                        IsValidLocalHeroesOffset((int)lh))
                    {
                        localHeroes = (int)lh;
                        applied.Add($"LocalHeroesOffset=0x{localHeroes:X}");
                    }

                    if (TryParseU32(f.HeroManagerOffset, out uint hmo) &&
                        IsValidHeroManagerOffset((int)hmo))
                    {
                        heroMgrOff = (int)hmo;
                        applied.Add($"HeroManagerOffset=0x{heroMgrOff:X}");
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
            itemBox = DefaultItemBoxOffset;
            localHeroes = DefaultLocalHeroesOffset;
            heroMgrOff  = DefaultHeroManagerOffset;
            stamp  = 0;
            outcome = "Saved file couldn't be read — it will be rebuilt automatically on the next scan";
        }

        _pawnVtableSeed = vtable;
        _maxPlausibleHp = maxHp;
        _maxTowerUnits  = maxTu;
        _itemBoxOffset  = itemBox;
        _localHeroesOffset = localHeroes;
        _heroManagerOffset = heroMgrOff;
        _gameStamp      = stamp;
        _status = outcome;
        Base.Log($"Tunables: {outcome} (file: {FilePath})");
    }

    // Discovered offsets must be small, dword-aligned struct offsets
    // inside their class-appropriate bands.
    private static bool IsValidItemBoxOffset(int off)
        => off >= MinItemBoxOffset && off <= MaxItemBoxOffset && (off & 3) == 0;

    private static bool IsValidLocalHeroesOffset(int off)
        => off >= MinLocalHeroesOffset && off <= MaxLocalHeroesOffset && (off & 3) == 0;

    private static bool IsValidHeroManagerOffset(int off)
        => off >= MinHeroManagerOffset && off <= MaxHeroManagerOffset && (off & 3) == 0;

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
                ItemBoxOffset  = $"0x{ItemBoxOffset:X}",
                LocalHeroesOffset = $"0x{LocalHeroesOffset:X}",
                HeroManagerOffset = $"0x{HeroManagerOffset:X}",
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
                // Fresh pin files carry ONLY discovered values. Seeding the
                // tuning defaults (MaxPlausibleHp/MaxTowerUnits) here froze
                // them at pin time — a later compiled-default change was then
                // silently overridden by every existing file (user-hit: the
                // 500M HP cap survived its own removal). WriteTemplate (an
                // explicit user action) still writes them for hand-editing.
                OverrideFile f = TryReadFileLocked() ?? new OverrideFile();
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

    // Auto-pin the discovered forge-box offset (ItemBoxEquipments off the
    // HeroManager). Called by the Settings CALIBRATE wizard and the Forge
    // Viewer self-heal the moment they relocate the box in live memory
    // after a DD1 patch — the "automatic updater" for the one struct field
    // that moves. Preserves the other keys; fail-safe + atomic; only writes
    // when the value actually changed (≈ once per patch). Unlike the vtable
    // seed there is no per-tick caller, so a lighter guard suffices.
    public static void PinItemBoxOffset(int discovered)
    {
        if (!IsValidItemBoxOffset(discovered)) return;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (discovered == _itemBoxOffset && File.Exists(FilePath)) return;
            if (DateTime.UtcNow < _pinRetryAfterUtc) return;
            try
            {
                // Fresh pin files carry ONLY discovered values. Seeding the
                // tuning defaults (MaxPlausibleHp/MaxTowerUnits) here froze
                // them at pin time — a later compiled-default change was then
                // silently overridden by every existing file (user-hit: the
                // 500M HP cap survived its own removal). WriteTemplate (an
                // explicit user action) still writes them for hand-editing.
                OverrideFile f = TryReadFileLocked() ?? new OverrideFile();
                f.ItemBoxOffset = $"0x{discovered:X}";
                // Only stamp a note if the file is otherwise fresh — don't
                // stomp the pawn-pin note when one is already present.
                f.Note ??= "Auto-pinned by GrandeuReforged: the forge-box (ItemBoxEquipments) " +
                           "offset was relocated in live memory after a game patch. Delete " +
                           "this key (or the file) to fall back to the compiled default + " +
                           "runtime re-discovery.";

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

                _itemBoxOffset = discovered;
                _status = $"Forge-box offset learned and saved — ItemBoxOffset=0x{discovered:X}";
                Base.Log($"Tunables: pinned ItemBoxOffset=0x{discovered:X}");
            }
            catch
            {
                _pinRetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
            }
        }
    }

    // Auto-pin the discovered hero-array pair base (LocalLoadedHeroes off
    // the HeroManager; ActiveHeroes is always +0xC above it). Same contract
    // as PinItemBoxOffset.
    public static void PinLocalHeroesOffset(int discovered)
    {
        if (!IsValidLocalHeroesOffset(discovered)) return;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (discovered == _localHeroesOffset && File.Exists(FilePath)) return;
            if (DateTime.UtcNow < _pinRetryAfterUtc) return;
            try
            {
                // Fresh pin files carry ONLY discovered values. Seeding the
                // tuning defaults (MaxPlausibleHp/MaxTowerUnits) here froze
                // them at pin time — a later compiled-default change was then
                // silently overridden by every existing file (user-hit: the
                // 500M HP cap survived its own removal). WriteTemplate (an
                // explicit user action) still writes them for hand-editing.
                OverrideFile f = TryReadFileLocked() ?? new OverrideFile();
                f.LocalHeroesOffset = $"0x{discovered:X}";
                f.Note ??= "Auto-pinned by GrandeuReforged: a game-class offset was " +
                           "relocated in live memory after a game patch. Delete this " +
                           "key (or the file) to fall back to the compiled default + " +
                           "runtime re-discovery.";
                WritePinnedFileLocked(f);
                _localHeroesOffset = discovered;
                _status = $"Hero-array offset learned and saved — LocalHeroesOffset=0x{discovered:X}";
                Base.Log($"Tunables: pinned LocalHeroesOffset=0x{discovered:X}");
            }
            catch
            {
                _pinRetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
            }
        }
    }

    // Auto-pin the discovered TheHeroManager hop (off the ViewportClient).
    // Same contract as PinItemBoxOffset.
    public static void PinHeroManagerOffset(int discovered)
    {
        if (!IsValidHeroManagerOffset(discovered)) return;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (discovered == _heroManagerOffset && File.Exists(FilePath)) return;
            if (DateTime.UtcNow < _pinRetryAfterUtc) return;
            try
            {
                // Fresh pin files carry ONLY discovered values. Seeding the
                // tuning defaults (MaxPlausibleHp/MaxTowerUnits) here froze
                // them at pin time — a later compiled-default change was then
                // silently overridden by every existing file (user-hit: the
                // 500M HP cap survived its own removal). WriteTemplate (an
                // explicit user action) still writes them for hand-editing.
                OverrideFile f = TryReadFileLocked() ?? new OverrideFile();
                f.HeroManagerOffset = $"0x{discovered:X}";
                f.Note ??= "Auto-pinned by GrandeuReforged: a game-class offset was " +
                           "relocated in live memory after a game patch. Delete this " +
                           "key (or the file) to fall back to the compiled default + " +
                           "runtime re-discovery.";
                WritePinnedFileLocked(f);
                _heroManagerOffset = discovered;
                _status = $"HeroManager hop learned and saved — HeroManagerOffset=0x{discovered:X}";
                Base.Log($"Tunables: pinned HeroManagerOffset=0x{discovered:X}");
            }
            catch
            {
                _pinRetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
            }
        }
    }

    // Shared atomic serialize + rename for the pin writers (lock held).
    private static void WritePinnedFileLocked(OverrideFile f)
    {
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
