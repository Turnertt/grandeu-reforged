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
// object list from GObjects, both found by the code signatures the SDK's
// DD_Basic.cpp uses (as does DupeReferenceResolver).
//
// Trust: nothing is returned by FieldOffset until, once per attach, three
// frozen UE3 engine fields (Pawn.Controller, PlayerController.Player,
// LocalPlayer.ViewportClient) resolve to their known hardcoded values —
// either on the local player's chain objects (EnsureTrusted) or on the
// engine UClass objects found in the walk (no character needed). If they
// don't, the reader stays off for that attach and callers fall back to
// content discovery — never worse than before. Read-only; all reads via
// Base.Instance.
internal static class GameReflection
{
    private const int ModuleBase = 0x00400000; // DD1 ships non-ASLR
    private const int OFF_OBJ_OUTER = 0x28, OFF_OBJ_NAME = 0x2C, OFF_OBJ_CLASS = 0x34;
    private const int OFF_FIELD_SUPER = 0x3C, OFF_FIELD_NEXT = 0x40;
    private const int OFF_STRUCT_CHILDREN = 0x4C, OFF_STRUCT_PROPSIZE = 0x50;
    private const int OFF_PROP_OFFSET = 0x64;

    // The engine UClass objects the trust check anchors on (exact UE3 class
    // names — each exists exactly once, loaded at engine start).
    private const string ClsPawn = "Pawn", ClsPlayerController = "PlayerController", ClsLocalPlayer = "LocalPlayer";

    private static readonly object _gate = new();
    private static (int pid, long gen) _session;
    private static int _namesData, _namesCount;
    private static int _objHeader;          // FArray header of GObjects (data, num, max) — re-read per walk
    private static bool _globalsSearched;
    private static bool? _trusted;
    private static string _status = "not checked yet";
    private static string _objectStatus = "not walked yet";
    private static int _liveHeroManager, _viewportClient;
    private static readonly List<int> _worldInfos = new();     // every non-default AWorldInfo seen in the last walk
    private static readonly Dictionary<int, string> _names = new();
    private static readonly Dictionary<int, string> _classNames = new();   // UClass* -> name
    private static readonly Dictionary<string, int> _classObjects = new(); // engine class name -> UClass*
    private static readonly Dictionary<(int cls, string field), int> _offsets = new();
    private static readonly Dictionary<(int cls, string field), (int offset, uint mask)> _boolFields = new();
    private static readonly Dictionary<(int cls, string name), bool> _classMatches = new(); // "is cls a subclass of name" (fully read chains only)

    public static string Status { get { lock (_gate) { SyncSession(); return _status; } } }

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
        _namesData = _namesCount = 0;
        _objHeader = 0;
        _globalsSearched = false;
        _trusted = null;
        _status = "not checked yet";
        _objectStatus = "not walked yet";
        _liveHeroManager = _viewportClient = 0;
        _worldInfos.Clear();
        _names.Clear();
        _classNames.Clear();
        _classObjects.Clear();
        _offsets.Clear();
        _boolFields.Clear();
        _classMatches.Clear();
    }

    // ── Object-list utilities for other readers ───────────────────────

    // GNames / GObjects as found for this attach (false = the object table
    // is unavailable). Lets DupeReferenceResolver skip its own 15 MB module
    // sweep when this reader already did it.
    public static bool TryGetGlobals(out int namesData, out int namesCount, out int objectsHeader)
    {
        lock (_gate)
        {
            SyncSession();
            bool ok = FindGlobalsLocked();
            namesData = _namesData; namesCount = _namesCount; objectsHeader = _objHeader;
            return ok && _objHeader != 0;
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
        for (; GameChain.IsGamePtr(c) && seen.Add(c) && seen.Count <= 32; c = GameChain.RdInt(c + OFF_FIELD_SUPER))
        {
            string? n = ClassNameLocked(c);
            if (n == null) return false; // torn — decide next time
            if (n == className) { match = true; break; }
        }
        if (match || c == 0) _classMatches[(cls, className)] = match;
        return match;
    }

    // Establish trust once per attach from the local player's chain objects.
    // A transient read failure leaves the verdict open (retried next scan);
    // only a real disagreement with the known engine offsets switches the
    // reader off for the attach.
    public static bool EnsureTrusted(int pawn, int controller, int player)
    {
        lock (_gate)
        {
            SyncSession();
            if (_trusted.HasValue) return _trusted.Value;
            if (!FindGlobalsLocked())
            {
                _trusted = false;
                _status = "unavailable — the game's name table was not found";
                Base.LogEvent("Reflection: " + _status);
                return false;
            }
            int a = OffsetLocked(pawn, "Controller");
            int b = OffsetLocked(controller, "Player");
            int c = OffsetLocked(player, "ViewportClient");
            if (a < 0 || b < 0 || c < 0) return false; // unreadable right now — decide later
            return DecideTrustLocked(a, b, c, "chain objects");
        }
    }

    // Same three anchors, read from the engine UClass objects the object
    // walk recorded — so trust can be established with no character at all.
    // Undecided (false, verdict left open) until a walk has seen all three.
    private static bool EnsureTrustedFromClassesLocked()
    {
        if (_trusted.HasValue) return _trusted.Value;
        if (!_classObjects.TryGetValue(ClsPawn, out int cPawn) ||
            !_classObjects.TryGetValue(ClsPlayerController, out int cPc) ||
            !_classObjects.TryGetValue(ClsLocalPlayer, out int cLp)) return false;
        int a = OffsetOfClassLocked(cPawn, "Controller");
        int b = OffsetOfClassLocked(cPc, "Player");
        int c = OffsetOfClassLocked(cLp, "ViewportClient");
        if (a < 0 || b < 0 || c < 0) return false;
        return DecideTrustLocked(a, b, c, "engine classes");
    }

    private static bool DecideTrustLocked(int a, int b, int c, string via)
    {
        bool ok = a == GameChain.OFF_PAWN_CONTROLLER && b == GameChain.OFF_CONTROLLER_PLAYER &&
                  c == GameChain.OFF_PLAYER_VIEWPORT;
        _trusted = ok;
        _status = ok ? $"verified (via {via})"
                     : $"off — disagrees with known engine offsets (Controller 0x{a:X}, Player 0x{b:X}, ViewportClient 0x{c:X})";
        if (!ok) { _offsets.Clear(); _boolFields.Clear(); }
        Base.LogEvent("Reflection: " + _status);
        return ok;
    }

    // Offset of a named field on obj's class or any superclass; -1 when the
    // reader is not trusted this attach or the field can't be resolved.
    public static int FieldOffset(int obj, string field)
    {
        lock (_gate)
        {
            SyncSession();
            if (_trusted == null) EnsureTrustedFromClassesLocked(); // a walk may have recorded the anchors already
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
                EnsureTrustedFromClassesLocked();
                return _liveHeroManager;
            }
            _liveHeroManager = 0;
            WalkObjectsLocked();
            EnsureTrustedFromClassesLocked();
            return _liveHeroManager;
        }
    }

    private static void WalkObjectsLocked()
    {
        if (!FindGlobalsLocked() || _objHeader == 0)
        {
            _objectStatus = "unavailable — the game's object table (GObjects) was not found";
            Base.LogEvent("Objects: " + _objectStatus);
            return;
        }
        // Re-read the header every walk: objects are created and destroyed
        // continuously, and the array itself can be reallocated when it grows.
        int data = GameChain.RdInt(_objHeader), count = GameChain.RdInt(_objHeader + 4);
        if (!GameChain.IsGamePtr(data) || count <= 0 || count > 2_000_000)
        {
            _objectStatus = $"unavailable — object table header unreadable (data=0x{data:X8} num={count})";
            Base.LogEvent("Objects: " + _objectStatus);
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var managers = new List<int>();
        _worldInfos.Clear();
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
                        // trust check anchors on (each exists exactly once).
                        string? n = ObjectNameLocked(obj);
                        if (n is ClsPawn or ClsPlayerController or ClsLocalPlayer) _classObjects.TryAdd(n, obj);
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
            EnsureTrustedFromClassesLocked();
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
    // trusted, the field isn't a BoolProperty, or the mask isn't one bit.
    public static (int offset, uint mask) BoolField(int obj, string field)
    {
        lock (_gate)
        {
            SyncSession();
            if (_trusted == null) EnsureTrustedFromClassesLocked();
            if (_trusted != true || !GameChain.IsGamePtr(obj)) return (-1, 0);
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

    private static (int offset, uint mask) ResolveBoolFieldLocked(int cls, string field)
    {
        int prop = FindPropertyLocked(cls, field, out int off);
        if (prop == 0 || off <= 0) return (-1, 0);
        if (ObjectNameLocked(GameChain.RdInt(prop + OFF_OBJ_CLASS)) != "BoolProperty") return (-1, 0);
        uint mask = (uint)GameChain.RdInt(prop + OFF_BOOL_BITMASK);
        if (mask == 0 || (mask & (mask - 1)) != 0) return (-1, 0);
        return (off, mask);
    }

    private const int OFF_BOOL_BITMASK = 0x84;

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

        var seenClasses = new HashSet<int>();
        for (int c = cls; GameChain.IsGamePtr(c) && seenClasses.Add(c) && seenClasses.Count <= 32;
             c = GameChain.RdInt(c + OFF_FIELD_SUPER))
        {
            int size = GameChain.RdInt(c + OFF_STRUCT_PROPSIZE);
            var seen = new HashSet<int>();
            int f = GameChain.RdInt(c + OFF_STRUCT_CHILDREN);
            while (GameChain.IsGamePtr(f) && seen.Add(f) && seen.Count <= 4000)
            {
                byte[]? h = Read(f, 0x44);
                if (h == null) return 0; // torn read — don't cache a miss
                if (NameLocked(System.BitConverter.ToInt32(h, OFF_OBJ_NAME)) == field &&
                    (ObjectNameLocked(System.BitConverter.ToInt32(h, OFF_OBJ_CLASS)) ?? "").EndsWith("Property"))
                {
                    int off = GameChain.RdInt(f + OFF_PROP_OFFSET);
                    // A field lies inside its declaring class's instance.
                    if (off <= 0 || off >= size || (off & 3) != 0) return 0;
                    _offsets[(cls, field)] = off;
                    offset = off;
                    return f;
                }
                f = System.BitConverter.ToInt32(h, OFF_FIELD_NEXT);
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

    private static string? NameLocked(int index)
    {
        if (_names.TryGetValue(index, out string? s)) return s;
        if (index < 0 || index >= _namesCount) return null;
        int entry = GameChain.RdInt(_namesData + index * 4);
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
                if (ch == 0) return _names[index] = System.Text.Encoding.Latin1.GetString(bytes.ToArray());
                bytes.Add(ch);
            }
        }
        return null;
    }

    // GNames + GObjects via the code signatures from the SDK's DD_Basic.cpp
    // (same as DupeReferenceResolver). One pass over the module per attach,
    // only on an explicit scan; names validated by index 0 reading "None".
    // Returns true when the NAME table is available (the object table is
    // optional — the chain route never needs it).
    private static bool FindGlobalsLocked()
    {
        if (_namesData != 0) return true;
        if (_globalsSearched) return false;
        _globalsSearched = true;

        int size = 0;
        byte[]? dos = Read(ModuleBase, 0x40);
        if (dos != null) size = GameChain.RdInt(ModuleBase + System.BitConverter.ToInt32(dos, 0x3C) + 0x50); // SizeOfImage
        if (size < 0x10000 || size > 128 * 1024 * 1024) size = 32 * 1024 * 1024;

        byte[] np = { 0x8B, 0x0D, 0, 0, 0, 0, 0x83, 0x3C, 0x81, 0x00, 0x74 };
        const string nm = "xx????xxxxx";
        byte[] op = { 0x8B, 0, 0, 0, 0, 0, 0x8B, 0x04, 0, 0x8B, 0x40, 0, 0x25, 0, 0x02, 0, 0 };
        const string om = "x?????xx?xx?xxxxx";
        for (int off = 0; off < size && (_namesData == 0 || _objHeader == 0); off += 0x10000)
        {
            byte[]? b = Read(ModuleBase + off, System.Math.Min(0x10000 + 32, size - off));
            if (b == null) continue;
            for (int i = 0; i + om.Length < b.Length; i++)
            {
                if (b[i] != 0x8B) continue;
                if (_namesData == 0 && Matches(b, i, np, nm))
                {
                    int hdr = System.BitConverter.ToInt32(b, i + 2);
                    int data = GameChain.RdInt(hdr), count = GameChain.RdInt(hdr + 4), max = GameChain.RdInt(hdr + 8);
                    if (GameChain.IsGamePtr(data) && count > 0 && count <= 2_000_000 && max >= count)
                    {
                        _namesData = data;
                        _namesCount = count;
                        if (NameLocked(0) != "None") { _namesData = _namesCount = 0; _names.Clear(); }
                    }
                }
                if (_objHeader == 0 && Matches(b, i, op, om))
                {
                    int hdr = System.BitConverter.ToInt32(b, i + 2);
                    int data = GameChain.RdInt(hdr), count = GameChain.RdInt(hdr + 4), max = GameChain.RdInt(hdr + 8);
                    if (GameChain.IsGamePtr(data) && count > 0 && count <= 2_000_000 && max >= count && max <= 2_000_000)
                        _objHeader = hdr;
                }
            }
            if ((off / 0x10000) % 16 == 15) System.Threading.Thread.Sleep(1); // share the game handle
        }
        return _namesData != 0;
    }

    private static bool Matches(byte[] b, int at, byte[] pat, string mask)
    {
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
