using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Modinator;

// Shared, read-only walk of the HeroManager pointer chain + small game
// memory helpers. One home for what ForgeViewerView, HeroViewerView and
// the Settings calibration wizard previously each duplicated. All reads
// via Base.Instance (the sanctioned Scanner path); never writes.
//
// Chain (DD1_INTERNALS.md §3, verified live across builds):
//   playerPawn +0x22C → ADunDefPlayerController
//              +0x3B8 → Player (ULocalPlayer)
//              +0x194 → ViewportClient (UDunDefViewportClient)
//              +0xCFC → TheHeroManager (UDunDefHeroManager)
internal static class GameChain
{
    // The first three hops are UE3 ENGINE-class offsets (APawn.Controller,
    // APlayerController.Player, ULocalPlayer.ViewportClient) — frozen since
    // the engine shipped; deliberately hardcoded.
    public const int OFF_PAWN_CONTROLLER     = 0x22C;
    public const int OFF_CONTROLLER_PLAYER   = 0x3B8;
    public const int OFF_PLAYER_VIEWPORT     = 0x194;
    // TheHeroManager off the ViewportClient. UDunDefViewportClient is a
    // GAME class — the insertion-fragile tier the forge box proved moves —
    // so this is a discovered + pinned default (Tunables.HeroManagerOffset),
    // not a trusted literal: ResolveHeroManager verifies the target by
    // CONTENT (hero arrays / forge box) and rescans the window on mismatch.
    // Trap next door: HeroManagerTemplate sits at +0xCF8 — a real
    // UDunDefHeroManager (same vtable!) whose arrays are empty archetype
    // defaults; the content gate is what rejects it.
    public const int OFF_VIEWPORT_HEROMGR    = 0xCFC; // last-known-good default; see HeroManagerOffset
    // LocalLoadedHeroes (+0x360) = the player's FULL local hero roster
    // (all saved heroes). It's a TArray<TScriptInterface<...>> — 8 bytes
    // per element, first dword is the UDunDefHero*. ActiveHeroes (+0x36C
    // = LocalLoadedHeroes + 0xC, the very next field) is only the in-play
    // hero(es). Verified live 2026-06-11 via the DLL memdump. Same fragile
    // game-class tier as the box → discovered + pinned as a PAIR
    // (Tunables.LocalHeroesOffset); read via ReadLocalHeroes /
    // ReadActiveHeroes, never via these literals.
    public const int OFF_HM_LOCALHEROES      = 0x360; // last-known-good default; see LocalHeroesOffset
    public const int OFF_HM_ACTIVEHEROES     = 0x36C; // always LocalHeroesOffset + 0xC
    // ItemBoxEquipments (the forge box), TArray<UHeroEquipment*>. This is
    // the ONE field on this chain that has moved on a DD1 patch (0x39C →
    // 0x3A8 in the 2026-06 build), so unlike everything else here it is not
    // trusted as a fixed literal: it is DISCOVERED from live memory and
    // pinned (Tunables.ItemBoxOffset / overrides.json), exactly like the
    // Auto-Kill vtable seed. 0x39C stays as the compiled last-known-good
    // default used until discovery learns otherwise. Read via ItemBoxOffset
    // / ReadItemBox below, never via this literal.
    public const int OFF_HM_ITEMBOX          = 0x39C; // last-known-good default; see ItemBoxOffset

    // Effective offsets for the three version-fragile game-class links,
    // each discovered from live content and pinned (overrides.json), else
    // the compiled last-known-good default.
    public static int ItemBoxOffset      => Tunables.ItemBoxOffset;
    public static int LocalHeroesOffset  => Tunables.LocalHeroesOffset;
    public static int ActiveHeroesOffset => Tunables.LocalHeroesOffset + 0xC;
    public static int HeroManagerOffset  => Tunables.HeroManagerOffset;

    public static int ResolveHeroManager(int playerPawn)
    {
        if (!IsGamePtr(playerPawn)) return 0;
        int controller = RdPtr(playerPawn + OFF_PAWN_CONTROLLER);
        int player     = RdPtr(controller + OFF_CONTROLLER_PLAYER);
        int vpClient   = RdPtr(player + OFF_PLAYER_VIEWPORT);
        if (!IsGamePtr(vpClient)) return 0;

        // Fast path: the pinned/default hop still points at an object that
        // verifiably CONTAINS the hero arrays or the forge box (this also
        // rejects the HeroManagerTemplate archetype one field below, whose
        // arrays are empty defaults).
        int off = HeroManagerOffset;
        int hm  = RdPtr(vpClient + off);
        if (LooksLikeHeroManager(hm)) return hm;

        // Self-heal: a patch inserted fields into UDunDefViewportClient and
        // moved the hop. Rescan the window for a pointer whose target
        // passes the content gate; pin the winner.
        int found = DiscoverHeroManagerOffset(vpClient);
        if (found != 0)
        {
            if (found != off) Tunables.PinHeroManagerOffset(found);
            return RdPtr(vpClient + found);
        }
        // Nothing verifiable (menu/loading — arrays legitimately empty, or
        // a transient stale read). Return the raw hop like the original
        // code did; callers already run staged diagnosis on failure.
        return hm;
    }

    // An object that reads as THE live HeroManager: real UObject vtable +
    // verifiable content (the hero-array pair or the forge box at their
    // pinned/default offsets). Content, not identity — an archetype/copy
    // with empty arrays fails.
    private static bool LooksLikeHeroManager(int hm)
    {
        if (!IsGamePtr(hm)) return false;
        uint vtable = (uint)RdInt(hm);
        if (vtable < 0x00400000u || vtable >= 0x02000000u) return false;
        return IsHeroPairAt(hm, LocalHeroesOffset) || IsItemBoxAt(hm, ItemBoxOffset);
    }

    // Window scanned for the TheHeroManager pointer, ViewportClient-
    // relative — ±0x100 around the known +0xCFC.
    private const int HeroMgrScanStart = 0xC00;
    private const int HeroMgrScanEnd   = 0xE00;
    // Adaptive fallback window — tried once when the primary misses.
    private const int HeroMgrScanWideStart = 0xA00;
    private const int HeroMgrScanWideEnd   = 0x1000;

    public static int DiscoverHeroManagerOffset(int vpClient)
    {
        int found = DiscoverHeroManagerIn(vpClient, HeroMgrScanStart, HeroMgrScanEnd);
        return found != 0 ? found
             : DiscoverHeroManagerIn(vpClient, HeroMgrScanWideStart, HeroMgrScanWideEnd);
    }

    private static int DiscoverHeroManagerIn(int vpClient, int scanStart, int scanEnd)
    {
        if (!IsGamePtr(vpClient)) return 0;
        int winLen = scanEnd - scanStart + 4;
        byte[]? win;
        try { win = Base.Instance.ReadMemory(vpClient + scanStart, winLen); }
        catch { win = null; }
        if (win == null || win.Length < winLen) return 0;

        // Pass 1 — cheap: the hop moved but the array offsets didn't.
        for (int off = scanStart; off <= scanEnd; off += 4)
        {
            int hm = System.BitConverter.ToInt32(win, off - scanStart);
            if (LooksLikeHeroManager(hm))
            {
                Base.Log($"HeroMgr: hop relocated to ViewportClient+0x{off:X} (content-verified)");
                return off;
            }
        }
        // Pass 2 — deep: the hop AND the array offsets moved in the same
        // patch. Accept a target in which the hero pair or the box is
        // discoverable anywhere in their own windows.
        for (int off = scanStart; off <= scanEnd; off += 4)
        {
            int hm = System.BitConverter.ToInt32(win, off - scanStart);
            if (!IsGamePtr(hm)) continue;
            uint vtable = (uint)RdInt(hm);
            if (vtable < 0x00400000u || vtable >= 0x02000000u) continue;
            if (DiscoverHeroArraysOffset(hm) != 0 || DiscoverItemBox(hm).pairVerified)
            {
                Base.Log($"HeroMgr: hop relocated to ViewportClient+0x{off:X} (deep-verified)");
                return off;
            }
        }
        return 0;
    }

    // Reads a UE3 TArray<T*> header (data ptr, Num, Max) and returns the
    // live element pointers. Defensive Num cap; bad reads yield an empty
    // list (a hero with no equipment, etc.).
    public static List<int> ReadPtrArray(int tarrayAddr) => ReadPtrArray(tarrayAddr, 4);

    // Stride-aware variant: each element is `stride` bytes and the element
    // pointer is its first dword. stride 4 = TArray<T*>; stride 8 =
    // TArray<TScriptInterface> (the {Object*, Interface*} pair, Object*
    // first) — e.g. LocalLoadedHeroes.
    public static List<int> ReadPtrArray(int tarrayAddr, int stride)
    {
        var result = new List<int>();
        int dataPtr = RdPtr(tarrayAddr);
        int num     = RdInt(tarrayAddr + 4);
        if (!IsGamePtr(dataPtr) || num <= 0 || num > 200000) return result;

        byte[]? arr;
        try { arr = Base.Instance.ReadMemory(dataPtr, num * stride); }
        catch { return result; }
        if (arr == null || arr.Length < num * stride) return result;

        for (int i = 0; i < num; i++)
        {
            int p = System.BitConverter.ToInt32(arr, i * stride);
            if (IsGamePtr(p)) result.Add(p);
        }
        return result;
    }

    // UE3 FString: data ptr, ArrayNum (chars incl. null), ArrayMax.
    public static string ReadFString(int addr)
    {
        try
        {
            int ptr = RdPtr(addr);
            int num = RdInt(addr + 4);
            if (!IsGamePtr(ptr) || num <= 1 || num > 256) return "";
            byte[]? b = Base.Instance.ReadMemory(ptr, (num - 1) * 2);
            if (b == null) return "";
            return System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0');
        }
        catch { return ""; }
    }

    // ── ItemBoxEquipments (forge box) — offset discovery + self-heal ──
    //
    // The forge-box offset is the one version-fragile field on the
    // HeroManager chain (a 2026-06 DD1 patch shifted it 0x39C → 0x3A8).
    // ReadItemBox returns the live UHeroEquipment* elements using the
    // pinned/default offset; if that offset no longer reads as THE box it
    // rediscovers the offset from live content, pins the winner, and uses
    // it — a patch self-heals with no user action (the same zero-touch
    // contract as the Auto-Kill seed). Caller adds +0x38 to each element
    // to reach the item's inline ItemNative.
    //
    // "Looks like equipment" is NOT sufficient to identify the box: the SDK
    // layout puts it in a cluster of other TArray<UHeroEquipment*> fields
    // whose elements are equally real equipment — ShopEquipments[3] (the
    // tavern shop pages, live-confirmed populated with 3 items each)
    // directly below, LobbyEquipments above. What distinguishes
    // ItemBoxEquipments is its neighbour: the very next field is
    // ItemBoxEntries (TArray<FItemBoxEntry>, stride 0x14 — one entry PER
    // REGISTERED USER, live-verified Num=1 solo against a 1114-item box,
    // NOT per-item as the SDK reading first suggested), i.e. a valid
    // NON-equipment TArray with Num > 0. A shop page fails that test — its
    // next field is another equipment array (the next page, or the box).
    // That adjacency fingerprint is required before anything is PINNED; a
    // score-only match is used for display at most, never saved.

    // Window scanned for the forge box, HeroManager-relative. Wide enough
    // to absorb several inserted OR removed fields around the known
    // positions (0x39C original, 0x3A8 since 2026-06); safe to overlap the
    // hero arrays (0x360/0x36C) and the shop pages because candidates must
    // pass the per-element equipment gate AND the fingerprint to be pinned.
    private const int ItemBoxScanStart  = 0x340;
    private const int ItemBoxScanEnd    = 0x480;
    // Adaptive fallback window — tried once when the primary window finds
    // no fingerprint-verified box, so a patch inserting more than the
    // primary absorbs still heals. Costs nothing in the common case.
    private const int ItemBoxScanWideStart = 0x280;
    private const int ItemBoxScanWideEnd   = 0x600;
    // Elements sampled per candidate — enough to be decisive without
    // marshalling a whole 1000-item box at every wrong offset.
    private const int ItemBoxSampleCount = 8;
    // Equipment-shaped elements required for a candidate (capped at Num for
    // tiny boxes). On top of this, ≥75 % of the sample must pass — a real
    // equipment array reads ~100 % live pointers, while a stride-mismatched
    // struct array (e.g. TArray<FDLCEquipmentEntry>) aliases to ≤50 %.
    private const int ItemBoxMinScore   = 2;
    // Floor for using a NON-fingerprinted candidate for display: the shop
    // pages / lobby lists are small fixed inventories, a forge box that
    // needs healing virtually never is. Never pinned regardless.
    private const int ItemBoxLooseMinCount = 25;

    public static List<int> ReadItemBox(int heroMgr)
    {
        if (!IsGamePtr(heroMgr)) return new List<int>();

        // Fast path: the pinned/default offset still reads as a populated
        // equipment array carrying the ItemBoxEntries fingerprint → it is
        // the box, no scan needed.
        int off = ItemBoxOffset;
        if (IsItemBoxAt(heroMgr, off))
            return ReadPtrArray(heroMgr + off);

        // Pinned offset no longer positively reads as the box: a patch
        // moved it, the box is simply empty (forge only populates in the
        // Tavern), or the fingerprint assumption broke. Rediscover.
        (int found, bool verified, int count) = DiscoverItemBox(heroMgr);

        if (verified)
        {
            if (found != off) Tunables.PinItemBoxOffset(found);
            return ReadPtrArray(heroMgr + found);
        }

        // No fingerprint-verified box anywhere (usually: box empty). The
        // loose candidate is DISPLAY-only — never pinned — and only used
        // when it clearly dominates whatever the current offset reads:
        // the floor keeps a shop page (small fixed inventory, observed
        // Max 17) from masquerading while the box is legitimately empty
        // mid-mission, and the dominance test keeps a stale pin that
        // landed on a small look-alike from hiding a large real box.
        List<int> current = ReadPtrArray(heroMgr + off);
        if (found != 0 && found != off &&
            count >= ItemBoxLooseMinCount && count > current.Count * 4)
        {
            Base.Log($"ItemBox: loose candidate +0x{found:X} ({count} elems) used for display — not pinned (no ItemBoxEntries fingerprint)");
            return ReadPtrArray(heroMgr + found);
        }
        return current;
    }

    // Does this HeroManager offset read as THE box right now — a populated
    // equipment array with the ItemBoxEntries fingerprint next door?
    private static bool IsItemBoxAt(int heroMgr, int off)
    {
        int data = RdPtr(heroMgr + off);
        int num  = RdInt(heroMgr + off + 4);
        int max  = RdInt(heroMgr + off + 8);
        if (!IsGamePtr(data) || num <= 0 || max < num || max > 200000) return false;
        return ElementsLookLikeEquipment(data, num) && HasEntriesFingerprint(heroMgr, off);
    }

    // Scan the HeroManager window for the ItemBoxEquipments TArray.
    // Returns the best candidate offset (0 = none), whether it carries the
    // ItemBoxEntries fingerprint (only then may it be pinned), and its
    // element count. Read-only; needs a populated box to succeed, so it
    // only fires in the Tavern — which is where calibration sends the user.
    public static (int offset, bool pairVerified, int count) DiscoverItemBox(int heroMgr)
    {
        var r = DiscoverItemBoxIn(heroMgr, ItemBoxScanStart, ItemBoxScanEnd);
        if (r.pairVerified) return r;
        var w = DiscoverItemBoxIn(heroMgr, ItemBoxScanWideStart, ItemBoxScanWideEnd);
        if (w.pairVerified) return w;
        return w.count > r.count ? w : r;
    }

    private static (int offset, bool pairVerified, int count) DiscoverItemBoxIn(
        int heroMgr, int scanStart, int scanEnd)
    {
        if (!IsGamePtr(heroMgr)) return (0, false, 0);

        // One read covers every candidate header in the window, plus 0x18
        // so the last candidate's partner header is inside the buffer.
        int winLen = scanEnd - scanStart + 0x18;
        byte[]? win;
        try { win = Base.Instance.ReadMemory(heroMgr + scanStart, winLen); }
        catch { win = null; }
        if (win == null || win.Length < winLen) return (0, false, 0);

        int bestOff = 0, bestNum = 0;           // fingerprint-verified tier
        int looseOff = 0, looseNum = 0;         // equipment-only tier
        for (int off = scanStart; off <= scanEnd; off += 4)
        {
            int i    = off - scanStart;
            int data = System.BitConverter.ToInt32(win, i);
            int num  = System.BitConverter.ToInt32(win, i + 4);
            int max  = System.BitConverter.ToInt32(win, i + 8);
            // Plausible populated TArray header (UE3 invariant: Num ≤ Max).
            if (!IsGamePtr(data) || num <= 0 || num > 200000 || max < num || max > 200000)
                continue;
            if (!ElementsLookLikeEquipment(data, num)) continue;

            // Fingerprint: the next field is a valid populated NON-equipment
            // TArray (ItemBoxEntries — per-user entries, Num ≥ 1). A shop
            // page fails it: its neighbour is another equipment array (the
            // next page, or the box itself). Live-verified 2026-07-02: box
            // num=1114 followed by entries num=1; num-equality does NOT
            // hold and must not be required.
            int pData = System.BitConverter.ToInt32(win, i + 0xC);
            int pNum  = System.BitConverter.ToInt32(win, i + 0x10);
            int pMax  = System.BitConverter.ToInt32(win, i + 0x14);
            bool pair = IsGamePtr(pData) && pNum > 0 && pMax >= pNum && pMax <= 200000 &&
                        !ElementsLookLikeEquipment(pData, pNum);

            Base.Log($"ItemBox: candidate HeroManager+0x{off:X} num={num} pair={pair}");
            if (pair) { if (num > bestNum)  { bestNum = num;  bestOff = off;  } }
            else      { if (num > looseNum) { looseNum = num; looseOff = off; } }
        }
        return bestOff != 0 ? (bestOff, true, bestNum) : (looseOff, false, looseNum);
    }

    // The pinned offset's fingerprint, from live reads (fast path — no
    // window scan): the next field is a valid populated NON-equipment
    // TArray (ItemBoxEntries, one entry per registered user). The
    // not-equipment leg is what rejects a shop page — its neighbour is
    // more equipment. Do NOT require Num equality with the box: entries
    // are per-user (live-verified Num=1 against a 1114-item box).
    private static bool HasEntriesFingerprint(int heroMgr, int off)
    {
        int pData = RdPtr(heroMgr + off + 0xC);
        int pNum  = RdInt(heroMgr + off + 0x10);
        int pMax  = RdInt(heroMgr + off + 0x14);
        return IsGamePtr(pData) && pNum > 0 && pMax >= pNum && pMax <= 200000 &&
               !ElementsLookLikeEquipment(pData, pNum);
    }

    // Do the first elements of this array read as live UHeroEquipment*?
    // Requires ≥ItemBoxMinScore hits (capped at Num) AND ≥75 % of the
    // sample, so stride-aliased struct arrays don't sneak past.
    private static bool ElementsLookLikeEquipment(int dataPtr, int num)
    {
        if (!IsGamePtr(dataPtr) || num <= 0) return false;
        int sample = num < ItemBoxSampleCount ? num : ItemBoxSampleCount;
        byte[]? arr;
        try { arr = Base.Instance.ReadMemory(dataPtr, sample * 4); }
        catch { return false; }
        if (arr == null || arr.Length < sample * 4) return false;

        int hits = 0;
        for (int i = 0; i < sample; i++)
            if (LooksLikeEquipment(System.BitConverter.ToInt32(arr, i * 4))) hits++;
        int needed = ItemBoxMinScore < sample ? ItemBoxMinScore : sample;
        return hits >= needed && hits * 4 >= sample * 3;
    }

    private static readonly int ItemNativeSize =
        System.Runtime.InteropServices.Marshal.SizeOf<ItemNative>();

    // A UHeroEquipment*: a real UObject (code-section vtable) whose inline
    // ItemNative (he + 0x38) passes the same sanity gates the forge's
    // bootstrap trusts for a genuine item. Strong enough that a hero object
    // or a garbage pointer landing here at a wrong offset scores ~0.
    private static bool LooksLikeEquipment(int he)
    {
        if (!IsGamePtr(he)) return false;
        uint vtable = (uint)RdInt(he);
        if (vtable < 0x00400000u || vtable >= 0x02000000u) return false; // code section

        int size = ItemNativeSize;
        byte[]? data;
        try { data = Base.Instance.ReadMemory(he + 0x38, size); }
        catch { return false; }
        if (data == null || data.Length < size) return false;

        ItemNative it;
        try { it = Base.Push<ItemNative>(data); } catch { return false; }

        // EquipmentTemplate is the archetype UObject pointer (heap, aligned).
        if ((uint)it.EquipmentTemplate < 0x100000u || (it.EquipmentTemplate & 3) != 0) return false;
        if (it.Level < 0 || it.Level > 500) return false;
        if (it.MaxEquipmentLevel <= 0 || it.MaxEquipmentLevel > 500) return false;
        if (it.MaxEquipmentLevel < it.Level) return false;
        if (!NativeArrayConsistent(it.EquipmentName)) return false;
        return true;
    }

    // ── Hero arrays (LocalLoadedHeroes + ActiveHeroes) — discovery ──
    //
    // Same insertion-fragile game-class tier as the forge box, and already
    // moved once: the 2026-06/07 build shifted the pair 0x360/0x36C →
    // 0x36C/0x378 (probe-verified — old 0x360 now holds the
    // NightmareDLCURL FString, whose char data masquerades as a plausible
    // TArray header). The fingerprint is the adjacent PAIR:
    // LocalLoadedHeroes is a DENSE stride-8 TArray<TScriptInterface> whose
    // objects read as UDunDefHero (sane level/cap at the HeroNative block
    // hero+0x504, consistent HeroName FString at hero+0x564), immediately
    // followed (+0xC) by ActiveHeroes — live-verified to be a SPARSE slot
    // array on the current build (num=40, mostly nulls, the in-play hero
    // at slot 0), so its leg of the gate requires every non-null sampled
    // element to be a hero and at least one non-null, NOT a dense 75 %.
    // Discovery pins the PAIR BASE (LocalLoadedHeroes); ActiveHeroes is
    // always base + 0xC. Needs at least one loaded + one in-play hero to
    // verify — true whenever a scan can reach the HeroManager at all.
    //
    // Note the gate anchors on UDunDefHero field offsets (0x504/0x564) —
    // themselves game-class values. If a patch shifts UDunDefHero, this
    // discovery fails soft (defaults keep being used, viewers show the
    // staged error) rather than mis-pinning; relocating the HeroNative
    // block inside the hero object would be its own discovery pass.
    private const int HeroPairScanStart = 0x2C0;
    private const int HeroPairScanEnd   = 0x440;
    // Adaptive fallback window — tried once when the primary misses.
    private const int HeroPairScanWideStart = 0x200;
    private const int HeroPairScanWideEnd   = 0x580;

    public static List<int> ReadLocalHeroes(int heroMgr)  => ReadHeroArray(heroMgr, activeOnly: false);
    public static List<int> ReadActiveHeroes(int heroMgr) => ReadHeroArray(heroMgr, activeOnly: true);

    private static List<int> ReadHeroArray(int heroMgr, bool activeOnly)
    {
        if (!IsGamePtr(heroMgr)) return new List<int>();
        int off = LocalHeroesOffset;
        if (!IsHeroPairAt(heroMgr, off))
        {
            int found = DiscoverHeroArraysOffset(heroMgr);
            if (found != 0)
            {
                if (found != off) Tunables.PinLocalHeroesOffset(found);
                off = found;
            }
            // else: keep the pinned/default offset — empty menus and
            // transient stale reads must not degrade a good pin.
        }
        return activeOnly ? ReadPtrArray(heroMgr + off + 0xC)
                          : ReadPtrArray(heroMgr + off, 8);
    }

    // The pinned offset's fingerprint, from live reads (fast path): a
    // dense populated stride-8 hero array immediately followed by the
    // sparse stride-4 active-hero slot array.
    private static bool IsHeroPairAt(int heroMgr, int off)
    {
        int data = RdPtr(heroMgr + off);
        int num  = RdInt(heroMgr + off + 4);
        int max  = RdInt(heroMgr + off + 8);
        if (!IsGamePtr(data) || num <= 0 || num > 1000 || max < num || max > 1000) return false;
        if (!ElementsLookLikeHeroes(data, num, 8)) return false;
        int pData = RdPtr(heroMgr + off + 0xC);
        int pNum  = RdInt(heroMgr + off + 0x10);
        int pMax  = RdInt(heroMgr + off + 0x14);
        if (!IsGamePtr(pData) || pNum <= 0 || pNum > 1000 || pMax < pNum || pMax > 1000) return false;
        return SparseSlotsLookLikeHeroes(pData, pNum);
    }

    // Scan the HeroManager window for the hero-array pair. Returns the
    // pair base offset (LocalLoadedHeroes; 0 = none), largest roster wins
    // a tie — which also rejects the stride-4 echo a hero array casts one
    // slot up (its "partner" is the next field, not a hero array, and its
    // roster is never larger than the real base's).
    public static int DiscoverHeroArraysOffset(int heroMgr)
    {
        int found = DiscoverHeroArraysIn(heroMgr, HeroPairScanStart, HeroPairScanEnd);
        return found != 0 ? found
             : DiscoverHeroArraysIn(heroMgr, HeroPairScanWideStart, HeroPairScanWideEnd);
    }

    private static int DiscoverHeroArraysIn(int heroMgr, int scanStart, int scanEnd)
    {
        if (!IsGamePtr(heroMgr)) return 0;
        int winLen = scanEnd - scanStart + 0x18;
        byte[]? win;
        try { win = Base.Instance.ReadMemory(heroMgr + scanStart, winLen); }
        catch { win = null; }
        if (win == null || win.Length < winLen) return 0;

        int bestOff = 0, bestNum = 0;
        for (int off = scanStart; off <= scanEnd; off += 4)
        {
            int i    = off - scanStart;
            int data = System.BitConverter.ToInt32(win, i);
            int num  = System.BitConverter.ToInt32(win, i + 4);
            int max  = System.BitConverter.ToInt32(win, i + 8);
            if (!IsGamePtr(data) || num <= 0 || num > 1000 || max < num || max > 1000) continue;
            if (!ElementsLookLikeHeroes(data, num, 8)) continue;
            int pData = System.BitConverter.ToInt32(win, i + 0xC);
            int pNum  = System.BitConverter.ToInt32(win, i + 0x10);
            int pMax  = System.BitConverter.ToInt32(win, i + 0x14);
            if (!IsGamePtr(pData) || pNum <= 0 || pNum > 1000 || pMax < pNum || pMax > 1000) continue;
            if (!SparseSlotsLookLikeHeroes(pData, pNum)) continue;

            Base.Log($"HeroArrays: candidate HeroManager+0x{off:X} local={num} active={pNum}");
            if (num > bestNum) { bestNum = num; bestOff = off; }
        }
        return bestOff;
    }

    // ActiveHeroes leg: a sparse slot array — nulls are normal (live: 40
    // slots, one in-play hero). Every NON-NULL sampled element must be a
    // hero and there must be at least one; a dense-percentage rule would
    // reject the real array.
    private static bool SparseSlotsLookLikeHeroes(int dataPtr, int num)
    {
        if (!IsGamePtr(dataPtr) || num <= 0) return false;
        int sample = num < 64 ? num : 64;
        byte[]? arr;
        try { arr = Base.Instance.ReadMemory(dataPtr, sample * 4); }
        catch { return false; }
        if (arr == null || arr.Length < sample * 4) return false;

        int nonNull = 0, hits = 0;
        for (int i = 0; i < sample; i++)
        {
            int p = System.BitConverter.ToInt32(arr, i * 4);
            if (!IsGamePtr(p)) continue;
            nonNull++;
            if (LooksLikeHero(p)) hits++;
        }
        return nonNull >= 1 && hits >= 1 && hits * 4 >= nonNull * 3;
    }

    // Do the first elements of this array (given stride; element pointer =
    // first dword) read as live UDunDefHero objects? Same ≥75 %-of-sample
    // rule as the equipment gate.
    private static bool ElementsLookLikeHeroes(int dataPtr, int num, int stride)
    {
        if (!IsGamePtr(dataPtr) || num <= 0) return false;
        int sample = num < 8 ? num : 8;
        byte[]? arr;
        try { arr = Base.Instance.ReadMemory(dataPtr, sample * stride); }
        catch { return false; }
        if (arr == null || arr.Length < sample * stride) return false;

        int hits = 0;
        for (int i = 0; i < sample; i++)
            if (LooksLikeHero(System.BitConverter.ToInt32(arr, i * stride))) hits++;
        int needed = 2 < sample ? 2 : sample;
        return hits >= needed && hits * 4 >= sample * 3;
    }

    // A UDunDefHero*: real UObject vtable + a sane HeroNative block —
    // HeroLevel (hero+0x52C) in the same 0..1000 band the Hero Viewer's
    // per-card gate trusts, a positive plausible HeroLevelCap (+0x530),
    // and a consistent HeroName FString header (+0x564).
    private static bool LooksLikeHero(int hero)
    {
        if (!IsGamePtr(hero)) return false;
        uint vtable = (uint)RdInt(hero);
        if (vtable < 0x00400000u || vtable >= 0x02000000u) return false;

        byte[]? d;
        try { d = Base.Instance.ReadMemory(hero + 0x504, 0x70); } // HeroNative window through HeroName
        catch { return false; }
        if (d == null || d.Length < 0x70) return false;

        int level = System.BitConverter.ToInt32(d, 0x28);  // hero+0x52C HeroLevel
        int cap   = System.BitConverter.ToInt32(d, 0x2C);  // hero+0x530 HeroLevelCap
        if (level < 0 || level > 1000) return false;
        if (cap <= 0 || cap > 10000) return false;

        int nPtr = System.BitConverter.ToInt32(d, 0x60);   // hero+0x564 HeroName FString
        int nLen = System.BitConverter.ToInt32(d, 0x64);
        int nMax = System.BitConverter.ToInt32(d, 0x68);
        if (nPtr == 0) return nLen == 0 && nMax == 0;
        if (!IsGamePtr(nPtr)) return false;
        return nLen >= 0 && nLen <= 4096 && nMax >= 0 && nMax <= 4096;
    }

    // Mirror of ForgeViewerView.NativeArrayConsistent — a UE3 FString/array
    // header is consistent only as (null,0,0) or (ptr, 0<len<=cap).
    private static bool NativeArrayConsistent(NativeArray na)
    {
        if (na.Address == 0) return na.CurrentLength == 0 && na.MaximumLength == 0;
        if (na.CurrentLength < 0 || na.CurrentLength > 4096) return false;
        if (na.MaximumLength < 0 || na.MaximumLength > 4096) return false;
        return true;
    }

    // DD1 is LARGEADDRESSAWARE on WOW64 — heap sits anywhere in
    // [0x01000000, 0xFFFE0000). Matches MainWindow.IsHeapPtr.
    public static bool IsGamePtr(int p)
        => (uint)p >= 0x01000000u && (uint)p < 0xFFFE0000u;

    public static int RdPtr(int addr)
    {
        if (!IsGamePtr(addr)) return 0;
        return RdInt(addr);
    }

    public static int RdInt(int addr)
    {
        try
        {
            byte[] b = Base.Instance.ReadMemory(addr, 4);
            return (b != null && b.Length >= 4) ? System.BitConverter.ToInt32(b, 0) : 0;
        }
        catch { return 0; }
    }

    // ── Game bitness ────────────────────────────────────────────────
    // The tool's whole memory model (4-byte pointers, x86 P/Invoke,
    // 0x00400000 base) only fits the 32-bit DD1 build. A 64-bit game
    // process is a hard "unsupported", and the single most useful thing
    // a failed scan can say.

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern System.IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(System.IntPtr h, out bool wow64);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(System.IntPtr h);

    // true = 32-bit game (supported), false = 64-bit game (unsupported),
    // null = game not running / undeterminable.
    public static bool? GameIs32Bit()
    {
        int pid = 0;
        var procs = System.Diagnostics.Process.GetProcessesByName("DunDefGame");
        if (procs.Length > 0) pid = procs[0].Id;
        foreach (var p in procs) { try { p.Dispose(); } catch { } }
        if (pid == 0) return null;

        // 32-bit OS can't run a 64-bit game at all.
        if (!System.Environment.Is64BitOperatingSystem) return true;

        System.IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == System.IntPtr.Zero) return null;
        try
        {
            if (!IsWow64Process(h, out bool wow64)) return null;
            return wow64; // WOW64 ⇒ 32-bit game on a 64-bit OS
        }
        finally { CloseHandle(h); }
    }

    // One staged, human-readable reason for a failed scan. `playerPawn`
    // is whatever the caller's pawn resolution returned (0 = none).
    public static string DescribeScanFailure(int playerPawn)
    {
        bool? is32 = GameIs32Bit();
        if (is32 == null)
            return "DunDefGame.exe is not running. Start Dungeon Defenders first.";
        if (is32 == false)
            return "This tool only works on the 32-bit version of Dungeon Defenders — " +
                   "the running game is 64-bit. Switch to the 32-bit build and rescan.";
        if (playerPawn == 0)
            return "No character found — the game looks like it's in a menu or loading screen. " +
                   "Go to the Tavern or a mission, then rescan. " +
                   "If that doesn't help, run CALIBRATE in Settings.";
        return "Couldn't reach the item manager from the player character " +
               "(a transient stale read is common right after a map change). " +
               "Rescan in a few seconds, or run CALIBRATE in Settings.";
    }
}
