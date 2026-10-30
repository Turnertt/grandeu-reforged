using System.Collections.Generic;

namespace Modinator;

// Reads DD1's own UE3 reflection data to learn where game-class fields live,
// by NAME, instead of inferring it from what the memory looks like — and,
// since 2026-09-27, walks the game's own object list (GObjects) to find the
// live HeroManager by IDENTITY instead of through the character.
//
// Why (2026-09-26): the content gates in GameChain locate the forge box by
// asking "do these elements look like normal items?". This tool exists to
// make items not look normal. A save full of heavily modded items failed that
// test, and the 3-item tavern shop page next door was taken for the box. The
// game itself records every field's offset: each UClass has a linked list of
// UProperty objects, and each UProperty carries its Offset. Reading that is
// not inference — it is the same data the SDK generator dumps, taken live.
//
// Why the object list (2026-09-27): the pawn→controller→player→viewport chain
// needs a character in a level, a WorldInfo found by a 4 GB region sweep
// (which skips regions ≥ 50 MB), and the right pawn picked out of the list.
// None of that is needed to read the forge: exactly one live
// UDunDefHeroManager exists per process, named Transient.DunDefHeroManager_N,
// and GObjects lists it. Walking ~250k object pointers takes ~0.3 s once per
// attach (live-measured), after which the cached hit is re-verified by name
// on every scan. The chain stays as the fallback.
//
// Layout on this engine (DD1_INTERNALS.md §3a, live-verified against all 20
// hardcoded chain offsets): UObject.Outer +0x28, Name (FName) +0x2C, Class
// +0x34; UField.SuperField +0x3C, Next +0x40; UStruct.Children +0x4C,
// PropertySize +0x50; UProperty.Offset +0x64. Names come from GNames and the
// object list from GObjects. Since 2026-10-28 both are found by a pin, every
// site of the SDK's code signatures and a scan of the exe's data section,
// each validated by content, and must agree (FindGlobalsLocked).
//
// Trust (2026-10-28, DECISIONS "reflection trust is self-referential"):
// nothing is returned by FieldOffset until, once per attach, the reader
// reads back Core.Object's own declared header (Outer 0x28, Name 0x2C, …)
// through the same Children/Next/Offset slots it uses for game classes. If
// that fails, those slots are re-derived by structural search and checked
// again; only a failed or ambiguous search turns the reader off for the
// attach, and callers fall back to content discovery — never worse than
// before. The three frozen engine fields (Pawn.Controller,
// PlayerController.Player, LocalPlayer.ViewportClient) are still compared
// with the tool's constants, but a mismatch is reported, not a gate.
// Read-only; all reads via Base.Instance.
internal static class GameReflection
{
    private const int ModuleBase = 0x00400000; // DD1 ships non-ASLR
    private const int OFF_OBJ_OUTER = 0x28, OFF_OBJ_NAME = 0x2C, OFF_OBJ_CLASS = 0x34;

    // Reflection-internal slots. UE3 licensees place these differently (BL2:
    // Next 0x3C, SuperField in UStruct at 0x48, Offset 0x60) and none of them
    // has a UProperty of its own ("NOT AUTO-GENERATED PROPERTY" in the SDK),
    // so they are proven per attach against Core.Object's self-description
    // and re-derived structurally when that fails (CalibrateCoreLocked).
    // Defaults = the live-verified DD1 layout. The UObject header offsets
    // above stay constant: every name read goes through them.
    private const int DefFieldSuper = 0x3C, DefFieldNext = 0x40;
    private const int DefStructChildren = 0x4C, DefStructPropSize = 0x50;
    private const int DefPropOffset = 0x64, DefBoolBitmask = 0x84;
    private static int _offFieldSuper = DefFieldSuper, _offFieldNext = DefFieldNext;
    private static int _offStructChildren = DefStructChildren, _offStructPropSize = DefStructPropSize;
    private static int _offPropOffset = DefPropOffset, _offBoolBitmask = DefBoolBitmask;
    // UProperty.ElementSize (DD1_INTERNALS §3a; live 2026-10-29: Byte 1,
    // Int 4, Name 8, Str 12, LinearColor 16 — the SDK does not name it).
    // Not calibrated: it is used only when Core.Object's header properties
    // read back their known sizes through it this attach (CoreSelfCheck);
    // otherwise the property-offset check keeps its 4-byte alignment rule.
    private const int DefPropElementSize = 0x48;
    private static bool _elementSizeUsable;

    // The engine UClass objects the trust check anchors on (exact UE3 class
    // names — each exists exactly once, loaded at engine start).
    private const string ClsPawn = "Pawn", ClsPlayerController = "PlayerController", ClsLocalPlayer = "LocalPlayer";

    private static readonly object _gate = new();
    private static (int pid, long gen) _session;
    private static int _namesData, _namesCount; // snapshot of GNames, refreshed from _namesHeader on a cache miss
    private static int _namesHeader;        // FArray header of GNames (data, num, max)
    private static int _objHeader;          // FArray header of GObjects (data, num, max) — re-read per walk
    private static bool _globalsSearched;
    private static string _globalsStatus = "not searched yet";
    private static long _objectsRetryAt;    // next object-table re-search while names are found but objects aren't
    private static int _objectsRetries;
    private static string _objectLogKey = ""; // last Objects: line logged, minus per-walk counts/timing
    private static bool? _trusted;
    private static string _status = "not checked yet";
    private static string _trustDetail = "";
    private static string _anchorNote = "";  // "" = not compared yet
    private static bool _boolFieldsUsable = true;
    private static readonly Dictionary<string, int> _coreClasses = new(); // Core.Object/Field/Struct/Property/BoolProperty, Engine.Actor
    private static string _objectStatus = "not walked yet";
    private static int _liveHeroManager, _viewportClient;
    private static readonly List<int> _worldInfos = new();     // every non-default AWorldInfo seen in the last walk
    private static readonly Dictionary<int, string> _names = new();
    private static readonly Dictionary<int, string> _classNames = new();   // UClass* -> name
    private static readonly Dictionary<string, int> _classObjects = new(); // engine class name -> UClass*
    private static readonly Dictionary<string, HashSet<int>> _classesByLeaf = new(); // class name -> every UClass* with it (last walk)
    private static readonly Dictionary<(int cls, string field), int> _offsets = new();
    private static bool _lastLookupTorn; // the last FindPropertyLocked miss was a torn read, not an absent field
    private static readonly Dictionary<(int cls, string field), (int offset, uint mask)> _boolFields = new();
    private static readonly Dictionary<(int cls, string name), bool> _classMatches = new(); // "is cls a subclass of name" (fully read chains only)

    public static string Status
    {
        get
        {
            lock (_gate)
            {
                SyncSession();
                return _anchorNote.StartsWith("MOVED", System.StringComparison.Ordinal) ? _status + "; engine anchors " + _anchorNote : _status;
            }
        }
    }

    // Multi-line trust report for COPY REPORT: how the reader was proven,
    // the slots in use, and whether the frozen engine fields still agree.
    public static string TrustDetail
    {
        get
        {
            lock (_gate)
            {
                SyncSession();
                string s = _status;
                if (_trustDetail.Length > 0) s += "\ncore   : " + _trustDetail;
                s += $"\nslots  : SuperField 0x{_offFieldSuper:X}  Next 0x{_offFieldNext:X}  Children 0x{_offStructChildren:X}  " +
                     $"PropertySize 0x{_offStructPropSize:X}  Offset 0x{_offPropOffset:X}  BitMask " +
                     (_boolFieldsUsable ? $"0x{_offBoolBitmask:X}" : "unproven (bool flags use compiled masks)");
                s += "\nanchors: " + (_anchorNote.Length > 0 ? _anchorNote : "not compared yet");
                return s;
            }
        }
    }

    // How GNames and GObjects were found this attach (pin / signatures /
    // data-section scan), any disagreement, and the scan timings. Runs the
    // bootstrap if it hasn't run yet.
    public static string GlobalsStatus { get { lock (_gate) { SyncSession(); FindGlobalsLocked(); return _globalsStatus; } } }

    // One line for the diagnostic report: was the object list found, how
    // long the walk took, what it found.
    public static string ObjectListStatus { get { lock (_gate) { SyncSession(); return _objectStatus; } } }

    // The live UDunDefViewportClient instance seen in the last walk (0 =
    // unknown). Lets GameChain keep the TheHeroManager hop pin in step with
    // the object-list route so every other reader agrees.
    public static int ViewportClient { get { lock (_gate) { SyncSession(); return _viewportClient; } } }

    // Everything is per attach: a restarted game has new heap addresses.
    private static void SyncSession()
    {
        var now = (Base.AttachedPid, Base.Instance.AttachmentGeneration);
        if (now == _session) return;
        _session = now;
        _namesData = _namesCount = _namesHeader = 0;
        _objHeader = 0;
        _globalsSearched = false;
        _globalsStatus = "not searched yet";
        _objectsRetryAt = 0;
        _objectsRetries = 0;
        _objectLogKey = "";
        _trusted = null;
        _trustRetryAt = 0;
        _status = "not checked yet";
        _trustDetail = _anchorNote = "";
        _boolFieldsUsable = true;
        ResetSlots();
        _coreClasses.Clear();
        _objectStatus = "not walked yet";
        _liveHeroManager = _viewportClient = 0;
        _worldInfos.Clear();
        _names.Clear();
        _classNames.Clear();
        _classObjects.Clear();
        _classesByLeaf.Clear();
        _offsets.Clear();
        _boolFields.Clear();
        _classMatches.Clear();
    }

    // The reader's verdict for this attach: null = undecided (waiting for
    // Core.u at the splash screen, or no walk yet), false = off.
    public static bool? Trusted
    {
        get { lock (_gate) { SyncSession(); if (_trusted == null) EnsureTrustedLocked(); return _trusted; } }
    }

    // ── Object-list utilities for other readers ───────────────────────

    // GNames / GObjects as found for this attach (false = the name table is
    // unavailable; objectsHeader is 0 when only the object table was not
    // found or was refused). DupeReferenceResolver's only source since
    // 2026-10-28 — its own first-hit module sweep was removed.
    public static bool TryGetGlobals(out int namesData, out int namesCount, out int objectsHeader)
    {
        lock (_gate)
        {
            SyncSession();
            bool ok = FindGlobalsLocked();
            // GNames grows (and can reallocate) as maps load. Callers index
            // it directly, so hand out the live header, not the attach-time
            // snapshot — names added since attach read as "Invalid object name".
            if (ok && Read(_namesHeader, 8) is byte[] h)
            {
                int data = System.BitConverter.ToInt32(h, 0), num = System.BitConverter.ToInt32(h, 4);
                if (GameChain.IsGamePtr(data) && num > 0 && num <= 2_000_000) { _namesData = data; _namesCount = num; }
            }
            namesData = _namesData; namesCount = _namesCount; objectsHeader = _objHeader;
            return ok;
        }
    }

    // Address of the GObjects FArray header {data, num, max} (0 = unknown).
    // A catalogue slot index plus this header is how floor sources prove an
    // actor is still the same registered object.
    public static int ObjectsHeader
    {
        get { lock (_gate) { SyncSession(); return FindGlobalsLocked() ? _objHeader : 0; } }
    }

    // Visit every live instance of `className` or one of its subclasses
    // (class leaf name, e.g. "DunDefDroppedEquipment"), skipping class
    // default objects. One pass over GObjects: per object a 4-byte class
    // read; class ancestry is resolved once per distinct UClass and cached.
    // ~0.3 s for ~250k objects live — this replaced a 256-per-chunk walk
    // that slept 1 ms per chunk (~15 s on a default timer). The visitor
    // runs under this reader's lock (re-entrant for the same thread, so it
    // may call FieldOffset / ObjectPath). Returns the visit count. Throws
    // InvalidOperationException when the object table is unavailable.
    public static int ForEachInstance(string className, System.Action<int, int, int> visit,
        System.IProgress<(int done, int total)>? progress, System.Threading.CancellationToken cancel)
    {
        lock (_gate)
        {
            SyncSession();
            if (!FindGlobalsLocked() || _objHeader == 0)
                throw new System.InvalidOperationException("The game's object table could not be found.");
            int data = GameChain.RdInt(_objHeader), count = GameChain.RdInt(_objHeader + 4);
            if (!GameChain.IsGamePtr(data) || count <= 0 || count > 2_000_000)
                throw new System.InvalidOperationException("The game's object table is unreadable right now.");

            int visited = 0;
            const int chunk = 4096;
            for (int i = 0; i < count; i += chunk)
            {
                cancel.ThrowIfCancellationRequested();
                int take = System.Math.Min(chunk, count - i);
                byte[]? arr = Read(data + i * 4, take * 4);
                if (arr != null)
                {
                    for (int j = 0; j < take; j++)
                    {
                        int obj = System.BitConverter.ToInt32(arr, j * 4);
                        if (!GameChain.IsGamePtr(obj)) continue;
                        int cls = GameChain.RdInt(obj + OFF_OBJ_CLASS);
                        if (!GameChain.IsGamePtr(cls) || !IsSubclassLocked(cls, className)) continue;
                        string? n = ObjectNameLocked(obj);
                        if (n == null || n.StartsWith("Default__", System.StringComparison.Ordinal)) continue;
                        visited++;
                        visit(obj, cls, i + j);
                    }
                }
                progress?.Report((System.Math.Min(i + chunk, count), count));
            }
            return visited;
        }
    }

    // Does `cls` derive from (or equal) the class named `className`? Walks
    // UField.SuperField; the verdict is cached only when the whole chain was
    // read (a torn read must not pin "no" on a class forever).
    private static bool IsSubclassLocked(int cls, string className)
    {
        if (_classMatches.TryGetValue((cls, className), out bool cached)) return cached;
        var seen = new HashSet<int>();
        int c = cls;
        bool match = false;
        for (; GameChain.IsGamePtr(c) && seen.Add(c) && seen.Count <= 32; c = GameChain.RdInt(c + _offFieldSuper))
        {
            string? n = ClassNameLocked(c);
            if (n == null) return false; // torn — decide next time
            if (n == className) { match = true; break; }
        }
        if (match || c == 0) _classMatches[(cls, className)] = match;
        return match;
    }

    // Establish trust once per attach from the local player's chain objects
    // (the route used when the object list is unavailable, and by COPY
    // REPORT). Core.Object is the root of the pawn's class chain; reaching
    // it by name through SuperField also proves that slot. When it can't be
    // reached that way, the object walk supplies it (and the classes the
    // calibration needs). Undecided (false, verdict left open) until then.
    public static bool EnsureTrusted(int pawn, int controller, int player)
    {
        lock (_gate)
        {
            SyncSession();
            if (!_trusted.HasValue)
            {
                if (!FindGlobalsLocked())
                {
                    // Undecided, not off: the bootstrap retries (a game still
                    // starting up has a name table too small to validate).
                    _status = "unavailable — the game's name table was not found";
                    return false;
                }
                int core = CoreFromChainLocked(GameChain.IsGamePtr(pawn) ? GameChain.RdInt(pawn + OFF_OBJ_CLASS) : 0);
                bool superProven = core != 0;
                if (core == 0 && _objHeader != 0 && !_coreClasses.ContainsKey("Object")) WalkObjectsLocked();
                if (core == 0) _coreClasses.TryGetValue("Object", out core);
                if (core == 0 || DecideTrustLocked(core, superProven) == null) return false;
            }
            if (_trusted == true)
                CompareAnchorsLocked(OffsetLocked(pawn, "Controller"), OffsetLocked(controller, "Player"),
                                     OffsetLocked(player, "ViewportClient"), "chain objects");
            return _trusted == true;
        }
    }

    // Same decision from the classes the object walk recorded — no
    // character needed. Undecided until a walk has seen Core.Object.
    private static bool EnsureTrustedLocked()
    {
        if (!_trusted.HasValue)
        {
            if (!_coreClasses.TryGetValue("Object", out int core)) return false;
            if (DecideTrustLocked(core, superProven: false) == null) return false;
        }
        if (_trusted == true && _classObjects.TryGetValue(ClsPawn, out int cPawn) &&
            _classObjects.TryGetValue(ClsPlayerController, out int cPc) &&
            _classObjects.TryGetValue(ClsLocalPlayer, out int cLp))
            CompareAnchorsLocked(OffsetOfClassLocked(cPawn, "Controller"), OffsetOfClassLocked(cPc, "Player"),
                                 OffsetOfClassLocked(cLp, "ViewportClient"), "engine classes");
        return _trusted == true;
    }

    // The root of a class's SuperField chain, when it is Core.Object.
    private static int CoreFromChainLocked(int cls)
    {
        var seen = new HashSet<int>();
        int c = cls;
        while (GameChain.IsGamePtr(c) && seen.Add(c) && seen.Count <= 32)
        {
            int super = GameChain.RdInt(c + _offFieldSuper);
            if (super == 0) return IsCoreClassLocked(c, "Object", "Core") ? c : 0;
            c = super;
        }
        return 0;
    }

    private static bool IsCoreClassLocked(int cls, string name, string package)
    {
        if (ObjectNameLocked(cls) != name) return false;
        int outer = GameChain.RdInt(cls + OFF_OBJ_OUTER);
        return GameChain.IsGamePtr(outer) && ObjectNameLocked(outer) == package &&
               GameChain.RdInt(outer + OFF_OBJ_OUTER) == 0;
    }

    // The trust verdict, once per attach. Core.Object's self-check first; on
    // failure the slots are calibrated and the check re-run. null = can't
    // tell yet (Core.Object unreadable, or Field/Property not seen yet).
    private static bool? DecideTrustLocked(int core, bool superProven)
    {
        if (_trusted.HasValue) return _trusted;
        if (_deciding || System.Environment.TickCount64 < _trustRetryAt) return null; // re-entered from the walk below / waiting
        _deciding = true;
        try { return DecideTrustCoreLocked(core, superProven); }
        finally { _deciding = false; }
    }

    private static bool _deciding;
    private static long _trustRetryAt;

    private static bool? DecideTrustCoreLocked(int core, bool superProven)
    {
        bool? ok = CoreSelfCheckLocked(core, superProven, out string detail);
        if (ok == null) return null;
        string via = "core self-check";
        if (ok == false)
        {
            string first = detail;
            // Calibration reads Core.Field/Struct/Property/BoolProperty and
            // Engine.Actor, which only the object walk records.
            if (!_coreClasses.ContainsKey("Field") && _objHeader != 0) WalkObjectsLocked();
            bool? calibrated = CalibrateCoreLocked(core, out string cal);
            if (calibrated == null)
            {
                // Nothing to calibrate against YET — not evidence of a wrong
                // layout. Core.Object's script properties load with Core.u,
                // after the native classes: at the splash screen (live
                // 10/2026, 128 objects) the self-check fails this way and an
                // "off" verdict would have stuck for the whole attach.
                _trustDetail = first;
                _status = "waiting — " + first + " (" + cal + "); retried every 5 s";
                _trustRetryAt = System.Environment.TickCount64 + 5_000;
                return null;
            }
            if (calibrated == true) ok = CoreSelfCheckLocked(core, superProven: false, out detail);
            if (ok != true)
            {
                ResetSlots();
                _trusted = false;
                _trustDetail = first;
                _status = "off — Core.Object self-check failed (" + first + ") and " +
                          (calibrated == true ? "the calibrated slots failed it too (" + detail + ")" : "calibration found no unique layout (" + cal + ")");
                _offsets.Clear(); _boolFields.Clear(); _classMatches.Clear();
                Base.LogEvent("Reflection: " + _status);
                return false;
            }
            via = "calibrated (" + cal + ")";
            // Everything cached so far was read through the old slots.
            _offsets.Clear(); _boolFields.Clear(); _classMatches.Clear();
        }
        _trusted = true;
        _trustDetail = detail;
        _status = $"verified ({via})";
        Base.LogEvent("Reflection: " + _status + " — " + detail);
        return true;
    }

    // Core.Object declares its own header as native script properties (SDK
    // DD_Core_classes.hpp). Reading them back through Children/Next/
    // UProperty.Offset proves those slots in one pass: they can only move
    // together with the header every name read already relies on. SuperField
    // is proven by the Field→Object and Property→Field links (or by the
    // caller having reached Core.Object through it). null = undecided.
    private static readonly (string name, int offset)[] CoreHeader =
    {
        ("ObjectInternalInteger", 0x04), ("Outer", 0x28), ("Name", 0x2C), ("Class", 0x34), ("ObjectArchetype", 0x38),
    };
    private const int CoreObjectSize = 0x3C;

    private static bool? CoreSelfCheckLocked(int core, bool superProven, out string detail)
    {
        byte[]? sz = Read(core + _offStructPropSize, 4);
        if (sz == null) { detail = "Core.Object unreadable"; return null; }
        int size = System.BitConverter.ToInt32(sz, 0);
        if (size != CoreObjectSize)
        {
            detail = $"Core.Object PropertySize reads 0x{size:X} at +0x{_offStructPropSize:X}, expected 0x{CoreObjectSize:X}";
            return false;
        }
        var props = ChildPropertiesLocked(core, _offStructChildren, _offFieldNext, out _);
        if (props == null) { detail = $"Core.Object's field list (Children +0x{_offStructChildren:X}, Next +0x{_offFieldNext:X}) is not a valid list"; return false; }
        bool sizesOk = true;
        foreach (var (name, offset) in CoreHeader)
        {
            int got = props.TryGetValue(name, out int p) ? GameChain.RdInt(p + _offPropOffset) : -1;
            if (got != offset)
            {
                detail = got < 0 ? $"Core.Object.{name} not in its field list"
                                 : $"Core.Object.{name} Offset reads 0x{got:X} at +0x{_offPropOffset:X}, expected 0x{offset:X}";
                return false;
            }
            // Name is an FName (8 bytes); the other four header fields are 4.
            if (GameChain.RdInt(p + DefPropElementSize) != (name == "Name" ? 8 : 4)) sizesOk = false;
        }
        _elementSizeUsable = sizesOk;
        if (!superProven)
        {
            if (!_coreClasses.TryGetValue("Field", out int field) || !_coreClasses.TryGetValue("Property", out int prop))
            {
                detail = "Core.Field / Core.Property not seen yet";
                return null;
            }
            if (GameChain.RdInt(core + _offFieldSuper) != 0 || GameChain.RdInt(field + _offFieldSuper) != core ||
                GameChain.RdInt(prop + _offFieldSuper) != field)
            {
                detail = $"SuperField +0x{_offFieldSuper:X} does not link Property→Field→Object";
                return false;
            }
        }
        detail = $"Core.Object PropertySize 0x{size:X}; " +
                 string.Join("  ", System.Array.ConvertAll(CoreHeader, h => $"{h.name} 0x{h.offset:X}")) +
                 (sizesOk ? "; ElementSize 0x48 proven" : "; ElementSize 0x48 unproven (byte fields not resolved)");
        return true;
    }

    // name -> UProperty* for every *Property node in a class's own field
    // list, read through the given slots. null = not a valid list (a node
    // that isn't a UField, a cycle, or no terminating null within the cap).
    // `nodes` = list length (properties, functions, consts, structs, …).
    private static Dictionary<string, int>? ChildPropertiesLocked(int cls, int childrenSlot, int nextSlot, out int nodes)
    {
        nodes = 0;
        var props = new Dictionary<string, int>();
        var seen = new HashSet<int>();
        int len = System.Math.Max(0x44, nextSlot + 4);
        int f = GameChain.RdInt(cls + childrenSlot);
        while (f != 0)
        {
            if (!GameChain.IsGamePtr(f) || !seen.Add(f) || seen.Count > 8000) return null;
            byte[]? h = Read(f, len);
            if (h == null) return null;
            string? cn = ClassNameLocked(System.BitConverter.ToInt32(h, OFF_OBJ_CLASS));
            if (cn == null || !IsFieldClassName(cn)) return null;
            if (cn.EndsWith("Property", System.StringComparison.Ordinal))
            {
                string? n = NameLocked(System.BitConverter.ToInt32(h, OFF_OBJ_NAME));
                if (n != null) props.TryAdd(n, f);
            }
            f = System.BitConverter.ToInt32(h, nextSlot);
        }
        nodes = seen.Count;
        return props;
    }

    // Every UField subclass in DD1's Core (SDK DD_Core_classes.hpp) — a
    // class's Children list holds nothing else.
    private static bool IsFieldClassName(string cn) =>
        cn.EndsWith("Property", System.StringComparison.Ordinal) ||
        cn is "Function" or "Const" or "Enum" or "ScriptStruct" or "Struct" or "State";

    // Re-derive the reflection slots from Core.Object's self-description
    // when the defaults fail the self-check (a licensee layout or a future
    // engine patch). Every slot must have exactly ONE solution, else the
    // calibration fails and the reader stays off. null = nothing to
    // calibrate against yet (the Core classes not recorded, or no layout
    // reaches Core.Object's header properties because they aren't loaded).
    // Needs the classes the object walk records. Read-only; nothing is
    // pinned to disk.
    private static bool? CalibrateCoreLocked(int core, out string detail)
    {
        if (!_coreClasses.TryGetValue("Field", out int field) || !_coreClasses.TryGetValue("Struct", out int strct) ||
            !_coreClasses.TryGetValue("Property", out int prop) || !_coreClasses.TryGetValue("BoolProperty", out int boolProp))
        {
            detail = "the object walk has not recorded Core.Field/Struct/Property/BoolProperty";
            return null;
        }

        // A. Children + Next: the one pair whose list reaches all five header
        //    properties, every node a UField, terminated.
        var lists = new List<(int children, int next, Dictionary<string, int> props)>();
        for (int children = 0x40; children <= 0x60; children += 4)
            foreach (int next in new[] { 0x3C, 0x40, 0x44, 0x48 })
            {
                if (next == children) continue;
                var p = ChildPropertiesLocked(core, children, next, out _);
                if (p != null && System.Array.TrueForAll(CoreHeader, h => p.ContainsKey(h.name))) lists.Add((children, next, p));
            }
        if (lists.Count == 0) { detail = "no Children/Next pair reaches Core.Object's header properties — not loaded yet?"; return null; }
        if (lists.Count > 1) { detail = $"{lists.Count} Children/Next candidates"; return false; }
        var (childrenSlot, nextSlot, coreProps) = lists[0];

        // B. UProperty.Offset: the one slot holding all five known offsets.
        var offsets = new List<int>();
        for (int slot = 0x40; slot <= 0x90; slot += 4)
            if (System.Array.TrueForAll(CoreHeader, h => GameChain.RdInt(coreProps[h.name] + slot) == h.offset)) offsets.Add(slot);
        if (offsets.Count != 1) { detail = $"{offsets.Count} UProperty.Offset candidates"; return false; }

        // C. SuperField: Object is the root; Field, Struct and Property derive
        //    from the class above them.
        var supers = new List<int>();
        foreach (int slot in new[] { 0x3C, 0x40, 0x44, 0x48 })
            if (GameChain.RdInt(core + slot) == 0 && GameChain.RdInt(field + slot) == core &&
                GameChain.RdInt(strct + slot) == field && GameChain.RdInt(prop + slot) == field &&
                GameChain.RdInt(boolProp + slot) == prop) supers.Add(slot);
        if (supers.Count != 1) { detail = $"{supers.Count} SuperField candidates"; return false; }

        // D. PropertySize: Object is 0x3C, Field a little more, Struct and
        //    Property more again, BoolProperty exactly Property + its BitMask.
        var sizes = new List<int>();
        for (int slot = 0x40; slot <= 0x5C; slot += 4)
        {
            if (slot == childrenSlot || slot == nextSlot || slot == supers[0]) continue;
            int fieldSize = GameChain.RdInt(field + slot), propSize = GameChain.RdInt(prop + slot);
            if (GameChain.RdInt(core + slot) == CoreObjectSize && fieldSize > CoreObjectSize && fieldSize <= 0x60 &&
                (fieldSize & 3) == 0 && GameChain.RdInt(strct + slot) > fieldSize && propSize > fieldSize &&
                propSize < 0x200 && GameChain.RdInt(boolProp + slot) == propSize + 4) sizes.Add(slot);
        }
        if (sizes.Count != 1) { detail = $"{sizes.Count} PropertySize candidates"; return false; }

        _offStructChildren = childrenSlot;
        _offFieldNext = nextSlot;
        _offPropOffset = offsets[0];
        _offFieldSuper = supers[0];
        _offStructPropSize = sizes[0];

        // E. UBoolProperty.BitMask is its only member, so it sits at
        //    UProperty's instance size. Proven on Engine.Actor's bools (one-bit
        //    masks, distinct within a dword); unproven → BoolField stays off
        //    and every caller uses its compiled mask.
        _offBoolBitmask = GameChain.RdInt(prop + _offStructPropSize);
        _boolFieldsUsable = BitMaskSlotHoldsLocked();
        detail = $"SuperField 0x{_offFieldSuper:X}, Next 0x{_offFieldNext:X}, Children 0x{_offStructChildren:X}, " +
                 $"PropertySize 0x{_offStructPropSize:X}, Offset 0x{_offPropOffset:X}, BitMask " +
                 (_boolFieldsUsable ? $"0x{_offBoolBitmask:X}" : "unproven");
        Base.LogEvent("Reflection: calibrated — " + detail);
        return true;
    }

    private static bool BitMaskSlotHoldsLocked()
    {
        if (!_coreClasses.TryGetValue("Actor", out int actor)) return false;
        var props = ChildPropertiesLocked(actor, _offStructChildren, _offFieldNext, out _);
        if (props == null) return false;
        var masks = new HashSet<(int offset, uint mask)>();
        int bools = 0;
        foreach (int p in props.Values)
        {
            if (ObjectNameLocked(GameChain.RdInt(p + OFF_OBJ_CLASS)) != "BoolProperty") continue;
            uint mask = (uint)GameChain.RdInt(p + _offBoolBitmask);
            if (mask == 0 || (mask & (mask - 1)) != 0 || !masks.Add((GameChain.RdInt(p + _offPropOffset), mask))) return false;
            bools++;
        }
        return bools >= 8;
    }

    private static void ResetSlots()
    {
        _offFieldSuper = DefFieldSuper; _offFieldNext = DefFieldNext;
        _offStructChildren = DefStructChildren; _offStructPropSize = DefStructPropSize;
        _offPropOffset = DefPropOffset; _offBoolBitmask = DefBoolBitmask;
        _elementSizeUsable = false;
    }

    // Pawn.Controller / PlayerController.Player / LocalPlayer.ViewportClient
    // against the tool's constants (the Auto-Kill / Mana / chain code still
    // uses them hardcoded). A mismatch means a patch moved an ENGINE field:
    // reported in Status and COPY REPORT, never a reason to distrust the
    // reader. Compared once per attach, when all three resolve.
    private static void CompareAnchorsLocked(int a, int b, int c, string via)
    {
        if (_anchorNote.Length > 0 || a < 0 || b < 0 || c < 0) return;
        var moved = new List<string>();
        if (a != GameChain.OFF_PAWN_CONTROLLER) moved.Add($"Pawn.Controller 0x{a:X} (tool uses 0x{GameChain.OFF_PAWN_CONTROLLER:X})");
        if (b != GameChain.OFF_CONTROLLER_PLAYER) moved.Add($"PlayerController.Player 0x{b:X} (tool uses 0x{GameChain.OFF_CONTROLLER_PLAYER:X})");
        if (c != GameChain.OFF_PLAYER_VIEWPORT) moved.Add($"LocalPlayer.ViewportClient 0x{c:X} (tool uses 0x{GameChain.OFF_PLAYER_VIEWPORT:X})");
        _anchorNote = moved.Count == 0
            ? $"agree (Controller 0x{a:X}, Player 0x{b:X}, ViewportClient 0x{c:X}; via {via})"
            : "MOVED — " + string.Join(", ", moved) + $" (via {via}); a patch moved an engine field the pawn chain, Auto-Kill and Mana still read at the old offset";
        if (moved.Count > 0) Base.LogEvent("Reflection: engine anchors " + _anchorNote);
    }

    // Offset of a named field on obj's class or any superclass; -1 when the
    // reader is not trusted this attach or the field can't be resolved.
    public static int FieldOffset(int obj, string field)
    {
        lock (_gate)
        {
            SyncSession();
            if (_trusted == null) EnsureTrustedLocked(); // a walk may have recorded Core.Object already
            return _trusted == true ? OffsetLocked(obj, field) : -1;
        }
    }

    // Is this address the live object the object list says it is? UE3 keeps
    // each object's slot index in UObject.ObjectInternalInteger (+0x4) and
    // GObjects[index] points back at it; a freed or recycled block fails
    // the round trip. Two reads. null = can't tell (no object table).
    public static bool? IsLiveObject(int obj)
    {
        lock (_gate)
        {
            SyncSession();
            return IsLiveObjectLocked(obj);
        }
    }

    private static bool? IsLiveObjectLocked(int obj)
    {
        if (!FindGlobalsLocked() || _objHeader == 0) return null;
        if (!GameChain.IsGamePtr(obj)) return false;
        int data = GameChain.RdInt(_objHeader), count = GameChain.RdInt(_objHeader + 4);
        if (!GameChain.IsGamePtr(data) || count <= 0) return null;
        int index = GameChain.RdInt(obj + OFF_OBJ_INDEX);
        if (index < 0 || index >= count) return false;
        return GameChain.RdInt(data + index * 4) == obj;
    }

    private const int OFF_OBJ_INDEX = 0x04; // UObject.ObjectInternalInteger (SDK DD_Core_classes.hpp)

    // The live HeroManager is created at engine start in the Transient
    // package ("Transient.DunDefHeroManager_0"). Its archetype
    // HeroManagerTemplate lives in the DunDefPlayers package, and the class
    // default object is "Default__DunDefHeroManager" — the two look-alikes
    // content checks have confused it with. null = can't tell (no names).
    public static bool? IsLiveHeroManager(int hm)
    {
        lock (_gate)
        {
            SyncSession();
            return IsLiveHeroManagerLocked(hm);
        }
    }

    private static bool? IsLiveHeroManagerLocked(int hm)
    {
        if (!FindGlobalsLocked() || !GameChain.IsGamePtr(hm)) return null;
        string? cls = ObjectNameLocked(GameChain.RdInt(hm + OFF_OBJ_CLASS));
        int outer = GameChain.RdInt(hm + OFF_OBJ_OUTER);
        string? outerName = GameChain.IsGamePtr(outer) ? ObjectNameLocked(outer) : null;
        if (cls == null || outerName == null) return null;
        return cls == "DunDefHeroManager" && outerName == "Transient" &&
               GameChain.RdInt(outer + OFF_OBJ_OUTER) == 0;
    }

    // ── Object-list route ─────────────────────────────────────────────
    //
    // The live HeroManager, found by walking GObjects — no character, no
    // WorldInfo, no pawn pick. Cached per attach; the cached object is
    // re-verified by name on every call, and a stale/missing hit triggers
    // one fresh walk. Also establishes reflection trust from the engine
    // UClass objects, so ReadItemBox can read the box by name straight
    // after. 0 = not available (object table not found, or no live manager
    // exists yet — e.g. before the engine finished starting).
    public static int FindLiveHeroManager()
    {
        lock (_gate)
        {
            SyncSession();
            if (_liveHeroManager != 0 && IsLiveHeroManagerLocked(_liveHeroManager) == true &&
                IsLiveObjectLocked(_liveHeroManager) != false)
            {
                EnsureTrustedLocked();
                return _liveHeroManager;
            }
            _liveHeroManager = 0;
            WalkObjectsLocked();
            EnsureTrustedLocked();
            return _liveHeroManager;
        }
    }

    private static void WalkObjectsLocked()
    {
        if (!FindGlobalsLocked() || _objHeader == 0)
        {
            _objectStatus = "unavailable — the game's object table (GObjects) was not found";
            LogObjectsLocked(_objectStatus);
            return;
        }
        // Re-read the header every walk: objects are created and destroyed
        // continuously, and the array itself can be reallocated when it grows.
        int data = GameChain.RdInt(_objHeader), count = GameChain.RdInt(_objHeader + 4);
        if (!GameChain.IsGamePtr(data) || count <= 0 || count > 2_000_000)
        {
            _objectStatus = $"unavailable — object table header unreadable (data=0x{data:X8} num={count})";
            LogObjectsLocked("unavailable — object table header unreadable");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var managers = new List<int>();
        _worldInfos.Clear();
        _classesByLeaf.Clear();
        int viewport = 0, seen = 0;
        const int chunk = 4096;
        for (int i = 0; i < count; i += chunk)
        {
            int take = System.Math.Min(chunk, count - i);
            byte[]? arr = Read(data + i * 4, take * 4);
            if (arr == null) continue; // a torn chunk is skipped, not fatal
            for (int j = 0; j < take; j++)
            {
                int obj = System.BitConverter.ToInt32(arr, j * 4);
                if (!GameChain.IsGamePtr(obj)) continue; // free slots are null
                int cls = GameChain.RdInt(obj + OFF_OBJ_CLASS);
                if (!GameChain.IsGamePtr(cls)) continue;
                seen++;
                string? cn = ClassNameLocked(cls);
                if (cn == null) continue;
                switch (cn)
                {
                    case "Class":
                    {
                        // A UClass object: record the engine classes the
                        // anchor comparison reads, and the Core classes the
                        // trust self-check / calibration read (each exists
                        // exactly once, loaded at engine start).
                        string? n = ObjectNameLocked(obj);
                        if (n != null)
                        {
                            if (!_classesByLeaf.TryGetValue(n, out var same)) _classesByLeaf[n] = same = new();
                            same.Add(obj);
                        }
                        if (n is ClsPawn or ClsPlayerController or ClsLocalPlayer) _classObjects.TryAdd(n, obj);
                        else if (n is "Object" or "Field" or "Struct" or "Property" or "BoolProperty" && IsCoreClassLocked(obj, n, "Core"))
                            _coreClasses.TryAdd(n, obj);
                        else if (n == "Actor" && IsCoreClassLocked(obj, n, "Engine")) _coreClasses.TryAdd(n, obj);
                        break;
                    }
                    case "DunDefHeroManager":
                        if (IsLiveHeroManagerLocked(obj) == true) managers.Add(obj);
                        break;
                    case "DunDefViewportClient":
                    {
                        string? n = ObjectNameLocked(obj);
                        if (n != null && !n.StartsWith("Default__", System.StringComparison.Ordinal)) viewport = obj;
                        break;
                    }
                    case "WorldInfo":
                    {
                        // Every level's AWorldInfo (the live one plus Kismet
                        // sublevel copies with no pawns — DD1_INTERNALS §3a).
                        // Which one is live is the caller's call: it owns the
                        // engine offsets (PawnList, TimeDilation, Game, GRI).
                        string? n = ObjectNameLocked(obj);
                        if (n != null && !n.StartsWith("Default__", System.StringComparison.Ordinal)) _worldInfos.Add(obj);
                        break;
                    }
                }
            }
        }
        sw.Stop();
        _viewportClient = viewport;

        // Exactly one live manager is the normal case (DD1_INTERNALS.md §3a:
        // default object, template, and the one Transient instance). Should
        // there ever be several, prefer the one the live viewport points at;
        // a wrong pick is never sticky because the cache is re-verified by
        // name and re-walked on every scan that finds it stale.
        int pick = managers.Count == 1 ? managers[0] : 0;
        if (managers.Count > 1)
        {
            EnsureTrustedLocked();
            int hop = viewport != 0 && _trusted == true ? OffsetLocked(viewport, "TheHeroManager") : -1;
            int viaViewport = hop > 0 ? GameChain.RdInt(viewport + hop) : 0;
            pick = managers.Contains(viaViewport) ? viaViewport : managers[0];
        }
        _liveHeroManager = pick;
        _objectStatus = $"walked {seen} objects in {sw.ElapsedMilliseconds} ms — " +
                        (pick != 0 ? $"live HeroManager 0x{pick:X8} '{ObjectPathLocked(pick)}'" : "no live HeroManager found") +
                        (managers.Count > 1 ? $" ({managers.Count} candidates)" : "") +
                        (viewport != 0 ? $", viewport 0x{viewport:X8}" : ", no viewport instance") +
                        (_classObjects.Count == 3 ? "" : $", engine anchors {_classObjects.Count}/3") +
                        $", {_worldInfos.Count} WorldInfo";
        LogObjectsLocked(_objectStatus.Substring(_objectStatus.IndexOf(" — ", System.StringComparison.Ordinal) + 3));
    }

    // Logs the walk's status only when `key` (the outcome, without the
    // object count and timing that differ every walk) changes.
    private static void LogObjectsLocked(string key)
    {
        if (key == _objectLogKey) return;
        _objectLogKey = key;
        Base.LogEvent("Objects: " + _objectStatus);
    }

    // Every non-default AWorldInfo object from the object list. Returned
    // from the last walk of this attach; `rewalk` forces a fresh walk (the
    // caller found none of them live — a map change replaced them). The
    // caller decides which is live; nothing here reads engine offsets.
    // Empty when the object table is unavailable.
    public static List<int> FindWorldInfos(bool rewalk)
    {
        lock (_gate)
        {
            SyncSession();
            if (rewalk || (_worldInfos.Count == 0 && _liveHeroManager == 0)) WalkObjectsLocked();
            return new List<int>(_worldInfos);
        }
    }

    // "Package.Outer.Name" for the diagnostic report ("?" when unreadable).
    public static string ObjectPath(int obj)
    {
        lock (_gate)
        {
            SyncSession();
            return ObjectPathLocked(obj);
        }
    }

    private static string ObjectPathLocked(int obj)
    {
        if (!FindGlobalsLocked()) return "?";
        var parts = new List<string>();
        var seen = new HashSet<int>();
        while (obj != 0 && parts.Count < 12 && seen.Add(obj))
        {
            parts.Add(ObjectNameLocked(obj) ?? "?");
            obj = GameChain.RdInt(obj + OFF_OBJ_OUTER);
        }
        parts.Reverse();
        return string.Join('.', parts);
    }

    // A bitfield flag by name: the dword offset AND the bit mask, both from
    // the game's UBoolProperty (BitMask at +0x84 — SDK DD_Core_classes.hpp
    // UBoolProperty, live-verified 2026-09-27: GRI IsLobbyLevel/
    // IsGameplayLevel both at +0x2C4 with masks 0x40000/0x80000, NOT the
    // bit 12/13 the tool had hardcoded). (-1, 0) when the reader is not
    // trusted, the field isn't a BoolProperty, the mask isn't one bit, or a
    // calibration could not prove where BitMask moved to.
    public static (int offset, uint mask) BoolField(int obj, string field)
    {
        lock (_gate)
        {
            SyncSession();
            if (_trusted == null) EnsureTrustedLocked();
            if (_trusted != true || !_boolFieldsUsable || !GameChain.IsGamePtr(obj)) return (-1, 0);
            // Cached per class: item naming asks for the same flag once per
            // item, and an uncached lookup walks the class's property list.
            int cls = GameChain.RdInt(obj + OFF_OBJ_CLASS);
            if (!GameChain.IsGamePtr(cls)) return (-1, 0);
            if (_boolFields.TryGetValue((cls, field), out var cached)) return cached;
            var result = ResolveBoolFieldLocked(cls, field);
            _boolFields[(cls, field)] = result;
            return result;
        }
    }

    // ── Class-keyed lookups (LayoutRegistry) ──────────────────────────
    //
    // FieldOffset needs an instance, and Main / DunDefPlayer / dropped items
    // only have one inside a level. These take the UClass itself, found by
    // package-qualified name from the last object walk — so the whole
    // registry can be checked at the main menu.

    // Re-walk the object list so ClassByName sees classes loaded since the
    // last walk (LayoutRegistry's retry after a "not loaded" verdict).
    public static void RefreshClasses()
    {
        lock (_gate) { SyncSession(); WalkObjectsLocked(); }
    }

    // The UClass `package.leaf`; 0 with a reason when it isn't loaded or
    // when more than one class carries that name (a mod package could add
    // one) — never a guess. Walks the object list once if nothing has yet.
    public static int ClassByName(string package, string leaf, out string why)
    {
        lock (_gate)
        {
            SyncSession();
            why = "";
            if (_classesByLeaf.Count == 0) WalkObjectsLocked();
            if (_classesByLeaf.Count == 0) { why = "object list unavailable"; return 0; }
            if (!_classesByLeaf.TryGetValue(leaf, out var all)) { why = $"class {package}.{leaf} is not loaded"; return 0; }
            int hit = 0, hits = 0;
            foreach (int c in all)
                if (OutermostNameLocked(c) == package) { hit = c; hits++; }
            if (hits == 1) return hit;
            why = hits == 0 ? $"class {leaf} is loaded, but not from package {package}" : $"{hits} classes named {package}.{leaf}";
            return 0;
        }
    }

    private static string? OutermostNameLocked(int obj)
    {
        for (int i = 0; i < 12 && GameChain.IsGamePtr(obj); i++)
        {
            int outer = GameChain.RdInt(obj + OFF_OBJ_OUTER);
            if (outer == 0) return ObjectNameLocked(obj);
            obj = outer;
        }
        return null;
    }

    // Offset of `field` on the class `cls` or a superclass: -1 when the
    // reader isn't trusted or no property by that name passed the checks;
    // `torn` = the miss was an unreadable node (retry later), not an answer.
    public static int ClassFieldOffset(int cls, string field, out bool torn)
    {
        lock (_gate)
        {
            SyncSession();
            torn = false;
            if (_trusted == null) EnsureTrustedLocked();
            if (_trusted != true) return -1;
            _lastLookupTorn = false;
            int off = OffsetOfClassLocked(cls, field);
            torn = off < 0 && _lastLookupTorn;
            return off;
        }
    }

    // BoolField by class: (-1, 0) as BoolField; `torn` as ClassFieldOffset.
    public static (int offset, uint mask) ClassBoolField(int cls, string field, out bool torn)
    {
        lock (_gate)
        {
            SyncSession();
            torn = false;
            if (_trusted == null) EnsureTrustedLocked();
            if (_trusted != true || !_boolFieldsUsable || !GameChain.IsGamePtr(cls)) return (-1, 0);
            if (_boolFields.TryGetValue((cls, field), out var cached)) return cached;
            _lastLookupTorn = false;
            var result = ResolveBoolFieldLocked(cls, field);
            if (result.offset < 0 && _lastLookupTorn) { torn = true; return result; } // don't cache a torn miss
            _boolFields[(cls, field)] = result;
            return result;
        }
    }

    // UStruct.PropertySize of a class (its instance size); -1 when untrusted.
    public static int ClassPropertySize(int cls)
    {
        lock (_gate)
        {
            SyncSession();
            if (_trusted == null) EnsureTrustedLocked();
            return _trusted == true && GameChain.IsGamePtr(cls) ? GameChain.RdInt(cls + _offStructPropSize) : -1;
        }
    }

    private static (int offset, uint mask) ResolveBoolFieldLocked(int cls, string field)
    {
        int prop = FindPropertyLocked(cls, field, out int off);
        if (prop == 0 || off <= 0) return (-1, 0);
        if (ObjectNameLocked(GameChain.RdInt(prop + OFF_OBJ_CLASS)) != "BoolProperty") return (-1, 0);
        uint mask = (uint)GameChain.RdInt(prop + _offBoolBitmask);
        if (mask == 0 || (mask & (mask - 1)) != 0) return (-1, 0);
        return (off, mask);
    }

    private static int OffsetLocked(int obj, string field)
    {
        if (!GameChain.IsGamePtr(obj)) return -1;
        int cls = GameChain.RdInt(obj + OFF_OBJ_CLASS);
        return OffsetOfClassLocked(cls, field);
    }

    // Offset of `field` declared on `cls` or any superclass. Cached per
    // (class, field); a torn read is never cached.
    private static int OffsetOfClassLocked(int cls, string field)
    {
        if (!GameChain.IsGamePtr(cls)) return -1;
        if (_offsets.TryGetValue((cls, field), out int cached)) return cached;
        FindPropertyLocked(cls, field, out int off);
        return off;
    }

    // The UProperty object for `field` on `cls` or any superclass (0 = not
    // found / torn read), with its validated Offset (cached per class+field).
    private static int FindPropertyLocked(int cls, string field, out int offset)
    {
        offset = -1;
        if (!GameChain.IsGamePtr(cls) || !FindGlobalsLocked()) return 0;

        int len = System.Math.Max(0x44, _offFieldNext + 4);
        var seenClasses = new HashSet<int>();
        for (int c = cls; GameChain.IsGamePtr(c) && seenClasses.Add(c) && seenClasses.Count <= 32;
             c = GameChain.RdInt(c + _offFieldSuper))
        {
            int size = GameChain.RdInt(c + _offStructPropSize);
            var seen = new HashSet<int>();
            int f = GameChain.RdInt(c + _offStructChildren);
            while (GameChain.IsGamePtr(f) && seen.Add(f) && seen.Count <= 4000)
            {
                byte[]? h = Read(f, len);
                if (h == null) { _lastLookupTorn = true; return 0; } // torn read — don't cache a miss
                if (NameLocked(System.BitConverter.ToInt32(h, OFF_OBJ_NAME)) == field &&
                    (ObjectNameLocked(System.BitConverter.ToInt32(h, OFF_OBJ_CLASS)) ?? "").EndsWith("Property"))
                {
                    int off = GameChain.RdInt(f + _offPropOffset);
                    // A field lies inside its declaring class's instance,
                    // aligned to its element: bytes anywhere, 2-byte
                    // elements on 2, everything wider on 4. Alignment is
                    // the largest power of two dividing the size, so a
                    // byte-only struct of 3 or 6 bytes isn't refused.
                    // Without a proven ElementSize, 4 for all (byte fields
                    // then don't resolve — the pre-2026-10-29 rule).
                    int align = 4;
                    if (_elementSizeUsable)
                    {
                        int es = GameChain.RdInt(f + DefPropElementSize);
                        if (es < 1 || es > 0x10000) { _lastLookupTorn = true; return 0; } // failed read — retry, don't cache
                        align = es % 4 == 0 ? 4 : es % 2 == 0 ? 2 : 1;
                    }
                    if (off <= 0 || off >= size || off % align != 0) return 0;
                    _offsets[(cls, field)] = off;
                    offset = off;
                    return f;
                }
                f = System.BitConverter.ToInt32(h, _offFieldNext);
            }
        }
        return 0;
    }

    private static string? ClassNameLocked(int cls)
    {
        if (_classNames.TryGetValue(cls, out string? cached)) return cached;
        string? n = ObjectNameLocked(cls);
        if (n != null) _classNames[cls] = n; // a torn read is retried, not cached
        return n;
    }

    private static string? ObjectNameLocked(int obj)
    {
        if (!GameChain.IsGamePtr(obj)) return null;
        byte[]? h = Read(obj + OFF_OBJ_NAME, 8);
        if (h == null) return null;
        string? n = NameLocked(System.BitConverter.ToInt32(h, 0));
        int number = System.BitConverter.ToInt32(h, 4); // UE3 stores instance number + 1
        return n == null ? null : number > 0 ? n + "_" + (number - 1) : n;
    }

    // Names are cached by index for the attach. A miss re-reads the GNames
    // header first: the table grows as maps load, and growing can move its
    // data array (a stale snapshot would read freed memory).
    private static string? NameLocked(int index)
    {
        if (_names.TryGetValue(index, out string? s)) return s;
        if (_namesHeader != 0 && Read(_namesHeader, 8) is byte[] h)
        {
            int data = System.BitConverter.ToInt32(h, 0), num = System.BitConverter.ToInt32(h, 4);
            if (GameChain.IsGamePtr(data) && num > 0 && num <= 2_000_000) { _namesData = data; _namesCount = num; }
        }
        string? n = DecodeName(_namesData, _namesCount, index);
        if (n != null) _names[index] = n;
        return n;
    }

    // FNameEntry: flags +0x4; the string inline at +0x10, or (flag 0x4000)
    // a pointer to it there. Uncached — the bootstrap decodes from
    // candidate tables that may not be the real one.
    private static string? DecodeName(int namesData, int namesCount, int index)
    {
        if (index < 0 || index >= namesCount) return null;
        int entry = GameChain.RdInt(namesData + index * 4);
        if (!GameChain.IsGamePtr(entry)) return null;
        int flags = GameChain.RdInt(entry + 4);
        int str = (flags & 0x4000) == 0 ? entry + 0x10 : GameChain.RdInt(entry + 0x10);
        var bytes = new List<byte>(32);
        for (int n = 0; n < 256; n += 32)
        {
            byte[]? part = Read(str + n, 32);
            if (part == null) return null;
            foreach (byte ch in part)
            {
                if (ch == 0) return System.Text.Encoding.Latin1.GetString(bytes.ToArray());
                bytes.Add(ch);
            }
        }
        return null;
    }

    // ── Bootstrap: GNames / GObjects ──────────────────────────────────
    //
    // Three independent witnesses, each validated by CONTENT before it
    // counts (DECISIONS "redundant, self-checking globals bootstrap"):
    //   pin        — the headers accepted last time (Tunables), honored only
    //                for the same exe build (PE TimeDateStamp);
    //   signatures — EVERY site of each code signature, resolved to the
    //                header it references. One global has dozens of sites
    //                (10/2026 build: GNames ×3, GObjects ×55 + A1 ×1), so
    //                agreement means one distinct validated header, not one
    //                match — the old sweep took the first hit;
    //   data scan  — every aligned {data, num, max} in the exe's writable
    //                sections that validates as the table (skipped when both
    //                pins validate). Survives a patch that breaks the
    //                signatures, since it needs no code bytes.
    // Two validated witnesses naming different headers = refused (the table
    // is reported unavailable and callers fall back to their content routes)
    // rather than a guess. A name table not found is retried after 15 s.
    // Returns true when the NAME table is available; the object table is
    // optional (the chain route never needs it).
    private static bool FindGlobalsLocked()
    {
        if (_namesHeader != 0)
        {
            // Names found but the object table not: usually an attach while
            // the game was still loading (GObjects isn't populated yet, so no
            // witness validates). That verdict used to stick for the whole
            // attach — floor items and every object-list route stayed dead
            // until the tool was restarted. Re-search the object table alone,
            // backing off 5 s → 60 s so a table that's genuinely gone costs
            // one search a minute.
            if (_objHeader == 0 && System.Environment.TickCount64 >= _objectsRetryAt)
                RetryObjectsLocked();
            return true;
        }
        if (_globalsSearched && System.Environment.TickCount64 < _globalsRetryAt) return false;
        _globalsSearched = true;
        _globalsRetryAt = System.Environment.TickCount64 + 15_000;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pe = ReadPeLayout();
        uint pinStamp = Tunables.GlobalsTimeDateStamp;
        bool pinUsable = pe.Stamp != 0 && pinStamp == pe.Stamp;
        int pnData = 0, pnCount = 0;
        int pinNames = pinUsable && ValidateNamesHeader((int)Tunables.GNamesHeader, out pnData, out pnCount) ? (int)Tunables.GNamesHeader : 0;
        int pinObjects = pinUsable && pinNames != 0 && ValidateObjectsHeader((int)Tunables.GObjectsHeader, pnData, pnCount) ? (int)Tunables.GObjectsHeader : 0;

        var sites = ScanSignatures(pe);
        long sigMs = sw.ElapsedMilliseconds;
        List<int>? dataNames = null, dataObjects = null;
        string dataNote = "skipped (both pins valid)";
        if (pinNames == 0 || pinObjects == 0)
        {
            if (!pe.Parsed) dataNote = "skipped (PE header unreadable)";
            else
            {
                ScanDataSections(pe, out dataNames, out dataObjects, out dataNote);
                dataNote += $" in {sw.ElapsedMilliseconds - sigMs} ms";
            }
        }

        // Names first: the object validator checks class names through them.
        var namesSigs = new Dictionary<int, string>();
        foreach (var (hdr, how) in sites.names)
            if (ValidateNamesHeader(hdr, out _, out _)) namesSigs[hdr] = how;
        int names = Agree(pinNames, namesSigs, dataNames, out string namesVerdict);
        int nData = 0, nCount = 0;
        if (names != 0) ValidateNamesHeader(names, out nData, out nCount);

        int objects = 0;
        string objectsVerdict = "not searched (no name table)";
        if (names != 0)
        {
            var objectsSigs = new Dictionary<int, string>();
            foreach (var (hdr, how) in sites.objects)
                if (ValidateObjectsHeader(hdr, nData, nCount)) objectsSigs[hdr] = how;
            objects = Agree(pinObjects, objectsSigs, dataObjects, out objectsVerdict);
        }

        _namesHeader = names;
        _namesData = nData; _namesCount = nCount;
        _objHeader = objects;
        _names.Clear();
        sw.Stop();
        string pinNote = pinStamp == 0 ? "" : !pinUsable ? $" (pin is for 0x{pinStamp:X8}, ignored)"
                       : pinNames == 0 || pinObjects == 0 ? " (pin failed validation, ignored)" : "";
        _globalsStatus = $"GNames {namesVerdict}\nGObjects {objectsVerdict}\n" +
                         $"build 0x{pe.Stamp:X8}{pinNote}; " +
                         $"signatures {sites.note} in {sigMs} ms; data scan {dataNote}; total {sw.ElapsedMilliseconds} ms";
        Base.LogEvent("Globals: " + _globalsStatus.Replace("\n", "; "));
        if (names != 0 && objects != 0 && pe.Stamp != 0) Tunables.PinGlobals((uint)names, (uint)objects, pe.Stamp);
        return names != 0;
    }

    private static long _globalsRetryAt;

    // The object-table half of FindGlobalsLocked, same witnesses and the
    // same agreement rule. Validated against the LIVE name table (it grows
    // while the game loads). Quiet on failure — the first search logged why.
    private static void RetryObjectsLocked()
    {
        _objectsRetries++;
        _objectsRetryAt = System.Environment.TickCount64 + System.Math.Min(60_000, 5_000L << System.Math.Min(_objectsRetries - 1, 4));
        try
        {
            if (!ValidateNamesHeader(_namesHeader, out int nData, out int nCount)) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pe = ReadPeLayout();
            bool pinUsable = pe.Stamp != 0 && Tunables.GlobalsTimeDateStamp == pe.Stamp;
            int pinObjects = pinUsable && ValidateObjectsHeader((int)Tunables.GObjectsHeader, nData, nCount) ? (int)Tunables.GObjectsHeader : 0;
            var sites = ScanSignatures(pe);
            List<int>? dataObjects = null;
            if (pinObjects == 0 && pe.Parsed) ScanDataSections(pe, out _, out dataObjects, out _);
            var objectsSigs = new Dictionary<int, string>();
            foreach (var (hdr, how) in sites.objects)
                if (ValidateObjectsHeader(hdr, nData, nCount)) objectsSigs[hdr] = how;
            int objects = Agree(pinObjects, objectsSigs, dataObjects, out string verdict);
            if (objects == 0) return;
            _objHeader = objects;
            _namesData = nData; _namesCount = nCount;
            string[] lines = _globalsStatus.Split('\n');
            if (lines.Length > 1) lines[1] = "GObjects " + verdict + $" (retry {_objectsRetries})";
            _globalsStatus = string.Join("\n", lines);
            Base.LogEvent($"Globals: GObjects {verdict} — found on retry {_objectsRetries} in {sw.ElapsedMilliseconds} ms");
            if (pe.Stamp != 0) Tunables.PinGlobals((uint)_namesHeader, (uint)objects, pe.Stamp);
        }
        catch { }
    }

    private static int Fail(out string verdict, string why) { verdict = why; return 0; }

    // The agreement rule (see FindGlobalsLocked). sigs: validated header ->
    // which signatures reached it; data: validated data-scan hits (null =
    // the scan did not run).
    private static int Agree(int pin, Dictionary<int, string> sigs, List<int>? data, out string verdict)
    {
        if (sigs.Count > 1)
            return Fail(out verdict, "refused — the signatures name " + sigs.Count + " different valid tables (" +
                                     string.Join(", ", System.Linq.Enumerable.Select(sigs, kv => $"0x{kv.Key:X8} via {kv.Value}")) + ")");
        int sig = 0; string how = "";
        foreach (var kv in sigs) { sig = kv.Key; how = kv.Value; }
        if (pin != 0 && sig != 0 && pin != sig)
            return Fail(out verdict, $"refused — pin 0x{pin:X8} but signatures 0x{sig:X8} ({how})");
        int pick = sig != 0 ? sig : pin;
        string dataPart = "";
        if (data != null)
        {
            if (pick != 0)
            {
                if (data.Count > 0 && !data.Contains(pick))
                    return Fail(out verdict, $"refused — {(sig != 0 ? "signatures" : "pin")} 0x{pick:X8} but data scan " +
                                             string.Join("/", data.ConvertAll(d => $"0x{d:X8}")));
                dataPart = data.Count == 0 ? ", data scan found none" : data.Count == 1 ? ", data scan agrees" : $", data scan agrees ({data.Count} hits)";
            }
            else if (data.Count == 1) { pick = data[0]; dataPart = "data scan only (signatures found nothing)"; }
            else return Fail(out verdict, data.Count == 0 ? "not found (no pin, no signature, no data-scan hit)"
                                                          : $"refused — data scan alone found {data.Count} tables");
        }
        else if (pick == 0) return Fail(out verdict, "not found (no pin, no signature hit)");
        verdict = $"0x{pick:X8} — " +
                  (sig != 0 ? "signatures " + how + (pin != 0 ? ", pin agrees" : "") : pin != 0 ? "pin" : "") + dataPart;
        return pick;
    }

    // {data, num, max} at hdr with the shape of a populated engine table
    // (num capped like the walk's count; max only loosely — slack policy is
    // the engine's business).
    private static bool IsTableShape(int data, int num, int max) =>
        GameChain.IsGamePtr(data) && (data & 3) == 0 && num >= 1000 && num <= 2_000_000 && num <= max && max <= 0x1000000;

    private static bool ReadTableHeader(int hdr, out int data, out int num)
    {
        data = num = 0;
        byte[]? h = GameChain.IsGamePtr(hdr) || (hdr >= ModuleBase && hdr < 0x01000000) ? Read(hdr, 12) : null;
        if (h == null) return false;
        data = System.BitConverter.ToInt32(h, 0); num = System.BitConverter.ToInt32(h, 4);
        return IsTableShape(data, num, System.BitConverter.ToInt32(h, 8));
    }

    // GNames: entry 0 is "None", UE3's hardcoded property-type names sit in
    // the first slots, and a spread sample of entries decodes as plain text.
    // The table is sparse — UE3 reserves fixed slots for its hardcoded
    // names, most of them unused (live 10/2026: 10 of 16 spread samples
    // null at the splash screen) — so only the non-null samples are judged.
    private static bool ValidateNamesHeader(int hdr, out int data, out int num)
    {
        if (!ReadTableHeader(hdr, out data, out num) || DecodeName(data, num, 0) != "None") return false;
        var early = new HashSet<string>();
        for (int i = 1; i <= 15; i++) if (DecodeName(data, num, i) is string s) early.Add(s);
        if (!early.Contains("ByteProperty") || !early.Contains("IntProperty") || !early.Contains("BoolProperty")) return false;
        int used = 0, printable = 0;
        for (int k = 0; k < 32; k++)
        {
            int i = (int)((long)(num - 1) * k / 31);
            if (GameChain.RdInt(data + i * 4) == 0) continue; // reserved, unused slot
            used++;
            string? s = DecodeName(data, num, i);
            if (s is { Length: > 0 } && System.Linq.Enumerable.All(s, ch => ch >= 0x20 && ch < 0x7F)) printable++;
        }
        return used >= 4 && printable >= used - 1;
    }

    // GObjects: the first live object in each of 16 spread windows sits at
    // the slot its ObjectInternalInteger names (the same round trip
    // IsLiveObject uses), and the class of its class is "Class" (UClass is
    // its own class). Windows, not single slots: the table can be very
    // sparse (live 10/2026 at the splash screen: 15 of 17 spread slots
    // null in 120k).
    private static bool ValidateObjectsHeader(int hdr, int namesData, int namesCount)
    {
        if (!ReadTableHeader(hdr, out int data, out int num)) return false;
        int used = 0, good = 0, someClass = 0;
        const int window = 1024; // one page of slots
        for (int k = 0; k < 16; k++)
        {
            int start = (int)((long)(num - 1) * k / 15);
            int take = System.Math.Min(window, num - start);
            byte[]? w = Read(data + start * 4, take * 4);
            if (w == null) continue;
            for (int j = 0; j < take; j++)
            {
                int p = System.BitConverter.ToInt32(w, j * 4);
                if (p == 0) continue;
                used++;
                if (GameChain.IsGamePtr(p) && GameChain.RdInt(p + OFF_OBJ_INDEX) == start + j &&
                    GameChain.IsGamePtr(GameChain.RdInt(p + OFF_OBJ_CLASS)))
                {
                    good++;
                    if (someClass == 0) someClass = GameChain.RdInt(p + OFF_OBJ_CLASS);
                }
                break;
            }
        }
        if (used < 2 || good * 5 < used * 4) return false; // ≥ 80%: a slot can be freed mid-sample
        int meta = GameChain.RdInt(someClass + OFF_OBJ_CLASS);
        if (!GameChain.IsGamePtr(meta) || GameChain.RdInt(meta + OFF_OBJ_CLASS) != meta) return false;
        return namesData == 0 || DecodeName(namesData, namesCount, GameChain.RdInt(meta + OFF_OBJ_NAME)) == "Class";
    }

    // Executable and writable section ranges of the exe image, and its
    // build stamp. Unparseable header: the whole image counts as code, the
    // header check falls back to "inside the image", and the data scan is
    // skipped (Parsed = false).
    private sealed class PeLayout
    {
        public readonly List<(int start, int end)> Exec = new(), Writable = new();
        public uint Stamp;
        public bool Parsed;
        public int ImageEnd;

        public bool HoldsHeader(int addr) =>
            (addr & 3) == 0 && (Parsed ? Writable.Exists(r => addr >= r.start && addr + 12 <= r.end)
                                       : addr >= ModuleBase && addr + 12 <= ImageEnd);
    }

    private static PeLayout ReadPeLayout()
    {
        var pe = new PeLayout();
        byte[]? dos = Read(ModuleBase, 0x40);
        int lfanew = dos != null && dos[0] == 'M' && dos[1] == 'Z' ? System.BitConverter.ToInt32(dos, 0x3C) : 0;
        byte[]? nt = lfanew is > 0 and < 0x1000 ? Read(ModuleBase + lfanew, 0x54) : null;
        int size = 0;
        if (nt != null && nt[0] == 'P' && nt[1] == 'E' && nt[2] == 0 && nt[3] == 0)
        {
            int sections = System.BitConverter.ToUInt16(nt, 6);
            pe.Stamp = System.BitConverter.ToUInt32(nt, 8);
            int optSize = System.BitConverter.ToUInt16(nt, 20);
            size = System.BitConverter.ToInt32(nt, 0x50); // SizeOfImage
            byte[]? tbl = sections is > 0 and <= 96 ? Read(ModuleBase + lfanew + 24 + optSize, sections * 40) : null;
            if (tbl != null)
                for (int s = 0; s < sections; s++)
                {
                    int vsize = System.BitConverter.ToInt32(tbl, s * 40 + 8), va = System.BitConverter.ToInt32(tbl, s * 40 + 12);
                    uint flags = System.BitConverter.ToUInt32(tbl, s * 40 + 36);
                    if (vsize <= 0 || va <= 0 || vsize > 0x4000000) continue;
                    var r = (ModuleBase + va, ModuleBase + va + vsize);
                    if ((flags & 0x20000000) != 0) pe.Exec.Add(r);     // IMAGE_SCN_MEM_EXECUTE
                    if ((flags & 0x80000000) != 0) pe.Writable.Add(r); // IMAGE_SCN_MEM_WRITE
                }
            pe.Parsed = pe.Exec.Count > 0 && pe.Writable.Count > 0;
        }
        if (size < 0x10000 || size > 128 * 1024 * 1024) size = 32 * 1024 * 1024;
        pe.ImageEnd = ModuleBase + size;
        if (!pe.Parsed) { pe.Exec.Clear(); pe.Writable.Clear(); pe.Exec.Add((ModuleBase, pe.ImageEnd)); }
        return pe;
    }

    // Code signatures for instructions that load the table headers. The
    // first two are the SDK's (DD_Basic.cpp); A1 is the `mov eax, [abs]`
    // form of the GObjects flag test (`and eax, 0x200` = RF_ flag on
    // ObjectFlags +0x8), found by static analysis of the 10/2026 build.
    private readonly record struct Sig(string Name, bool Names, byte[] Pat, string Mask, int OperandAt);

    private static readonly Sig[] Sigs =
    {
        new("#1", true, new byte[] { 0x8B, 0x0D, 0, 0, 0, 0, 0x83, 0x3C, 0x81, 0x00, 0x74 }, "xx????xxxxx", 2),
        new("#1", false, new byte[] { 0x8B, 0, 0, 0, 0, 0, 0x8B, 0x04, 0, 0x8B, 0x40, 0, 0x25, 0, 0x02, 0, 0 }, "x?????xx?xx?xxxxx", 2),
        new("A1", false, new byte[] { 0xA1, 0, 0, 0, 0, 0x8B, 0, 0, 0x8B, 0, 0, 0x25, 0, 0x02, 0, 0 }, "x????x??x??xxxxx", 1),
    };

    // Every site of every signature over the executable sections, grouped
    // by the header each references (must lie in a writable section).
    // Values: "#1 ×55 + A1 ×1".
    private static (Dictionary<int, string> names, Dictionary<int, string> objects, string note) ScanSignatures(PeLayout pe)
    {
        var counts = new Dictionary<(int hdr, bool names), Dictionary<string, int>>();
        int sites = 0;
        const int chunk = 1 << 20, overlap = 32;
        foreach (var (start, end) in pe.Exec)
            for (int at = start; at < end; at += chunk)
            {
                int len = System.Math.Min(chunk + overlap, end - at);
                byte[]? b = ReadLoose(at, len);
                if (b == null) continue;
                int stop = System.Math.Min(chunk, len);
                for (int i = 0; i < stop; i++)
                {
                    byte first = b[i];
                    if (first != 0x8B && first != 0xA1) continue;
                    foreach (var sig in Sigs)
                    {
                        if (sig.Pat[0] != first || !Matches(b, i, sig.Pat, sig.Mask)) continue;
                        // 8B /r with a disp32-only ModRM: the operand really is
                        // an absolute address.
                        if (first == 0x8B && (b[i + 1] & 0xC7) != 0x05) continue;
                        int hdr = System.BitConverter.ToInt32(b, i + sig.OperandAt);
                        if (!pe.HoldsHeader(hdr)) continue;
                        sites++;
                        if (!counts.TryGetValue((hdr, sig.Names), out var bySig)) counts[(hdr, sig.Names)] = bySig = new();
                        bySig[sig.Name] = bySig.GetValueOrDefault(sig.Name) + 1;
                    }
                }
            }
        var names = new Dictionary<int, string>();
        var objects = new Dictionary<int, string>();
        foreach (var ((hdr, isNames), bySig) in counts)
            (isNames ? names : objects)[hdr] = string.Join(" + ", System.Linq.Enumerable.Select(bySig, kv => $"{kv.Key} ×{kv.Value}"));
        return (names, objects, $"{sites} sites → {names.Count} names / {objects.Count} objects header(s)");
    }

    // Every 4-aligned {data, num, max} in the writable sections that
    // validates as GNames or GObjects. Header shape is checked from the
    // section bytes already read; only the survivors cost extra reads.
    private static void ScanDataSections(PeLayout pe, out List<int> names, out List<int> objects, out string note)
    {
        names = new List<int>();
        objects = new List<int>();
        var candidates = new List<int>();
        const int chunk = 256 * 1024, cap = 4096;
        foreach (var (start, end) in pe.Writable)
            for (int at = start; at < end && candidates.Count < cap; at += chunk)
            {
                int len = System.Math.Min(chunk + 8, end - at);
                byte[]? b = ReadLoose(at, len);
                if (b == null) continue;
                int stop = System.Math.Min(chunk, len - 12);
                for (int i = 0; i <= stop && candidates.Count < cap; i += 4)
                    if (IsTableShape(System.BitConverter.ToInt32(b, i), System.BitConverter.ToInt32(b, i + 4), System.BitConverter.ToInt32(b, i + 8)))
                        candidates.Add(at + i);
            }
        int nData = 0, nCount = 0;
        foreach (int c in candidates)
            if (ValidateNamesHeader(c, out int d, out int n)) { names.Add(c); if (nData == 0) { nData = d; nCount = n; } }
        foreach (int c in candidates)
            if (!names.Contains(c) && ValidateObjectsHeader(c, nData, nCount)) objects.Add(c);
        note = $"{candidates.Count}{(candidates.Count >= cap ? " (capped)" : "")} candidates → {names.Count} names / {objects.Count} objects";
    }

    // A read that survives an unreadable page inside the range: on failure
    // it re-reads page by page and leaves the holes zeroed. null only when
    // nothing at all was readable.
    private static byte[]? ReadLoose(int addr, int len)
    {
        byte[]? whole = Read(addr, len);
        if (whole != null) return whole;
        var b = new byte[len];
        bool any = false;
        for (int off = 0; off < len;)
        {
            int pageEnd = ((addr + off) & ~0xFFF) + 0x1000;
            int take = System.Math.Min(pageEnd - (addr + off), len - off);
            byte[]? part = Read(addr + off, take);
            if (part != null) { System.Buffer.BlockCopy(part, 0, b, off, take); any = true; }
            off += take;
        }
        return any ? b : null;
    }

    private static bool Matches(byte[] b, int at, byte[] pat, string mask)
    {
        if (at + mask.Length > b.Length) return false;
        for (int k = 0; k < mask.Length; k++)
            if (mask[k] == 'x' && b[at + k] != pat[k]) return false;
        return true;
    }

    private static byte[]? Read(int addr, int len)
    {
        try
        {
            byte[] b = Base.Instance.ReadMemory(addr, len);
            return b != null && b.Length >= len ? b : null;
        }
        catch { return null; }
    }
}
