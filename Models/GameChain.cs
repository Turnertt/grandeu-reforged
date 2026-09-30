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

    // The live HeroManager through the game's own object list — by class
    // and package name, with no character, WorldInfo sweep or pawn pick
    // involved (2026-09-27; GameReflection.FindLiveHeroManager). This is
    // the primary route for Forge / Hero: it works in menus, on a client,
    // with a modded-HP hero the pawn gates would reject, and when the
    // player's pawn sits in a region the sweep skips. Keeps the pinned
    // TheHeroManager hop in step so the chain route and the diagnostic
    // report agree. 0 = unavailable → callers fall back to the chain.
    public static int ResolveHeroManagerByObjectList()
    {
        int hm = GameReflection.FindLiveHeroManager();
        if (!IsGamePtr(hm)) return 0;
        int vp = GameReflection.ViewportClient;
        int hop = vp != 0 ? GameReflection.FieldOffset(vp, "TheHeroManager") : -1;
        if (hop > 0 && RdPtr(vp + hop) == hm && hop != HeroManagerOffset) Tunables.PinHeroManagerOffset(hop);
        Base.Log($"Chain: HeroManager 0x{hm:X8} via object list (hop {(hop > 0 ? $"+0x{hop:X}" : "unknown")})");
        return hm;
    }

    public static int ResolveHeroManager(int playerPawn)
    {
        // Object-list route first: identity by name beats every content
        // check below, and it needs none of the chain. The chain remains the
        // fallback for a build where the object table can't be found.
        int byList = ResolveHeroManagerByObjectList();
        if (byList != 0) return byList;

        if (!IsGamePtr(playerPawn)) { Base.Log("Chain: playerPawn=0 (no character resolved)"); return 0; }
        int controller = RdPtr(playerPawn + OFF_PAWN_CONTROLLER);
        int player     = RdPtr(controller + OFF_CONTROLLER_PLAYER);
        int vpClient   = RdPtr(player + OFF_PLAYER_VIEWPORT);
        // Debug-only (Base.Log is [Conditional("DEBUG")]): the per-hop dump
        // is what tells a remote report WHICH link died. A null Controller
        // on every pawn is the client signature — UE3 doesn't replicate
        // Pawn.Controller — and it takes out Mana + Forge + Hero together
        // while leaving Auto-Kill and Max Tower Units working.
        Base.Log($"Chain: pawn=0x{playerPawn:X8} +0x{OFF_PAWN_CONTROLLER:X}=0x{controller:X8} " +
                 $"+0x{OFF_CONTROLLER_PLAYER:X}=0x{player:X8} +0x{OFF_PLAYER_VIEWPORT:X}=0x{vpClient:X8}");
        if (!IsGamePtr(vpClient))
        {
            Base.Log("Chain: BROKE at " + (!IsGamePtr(controller) ? "Pawn.Controller (+0x22C) — null/invalid; " +
                     "expected on a CLIENT (not hosting)" : !IsGamePtr(player)
                     ? "PlayerController.Player (+0x3B8) — this pawn is not the local player"
                     : "LocalPlayer.ViewportClient (+0x194)"));
            return 0;
        }

        // Reflection first: the game's own UProperty table names the hop, and
        // the target is identified by NAME (Transient.DunDefHeroManager_*),
        // not by what its arrays contain. Falls through to the content
        // checks below when reflection is unavailable or can't confirm.
        if (GameReflection.EnsureTrusted(playerPawn, controller, player))
        {
            int rHop = GameReflection.FieldOffset(vpClient, "TheHeroManager");
            int rHm  = rHop > 0 ? RdPtr(vpClient + rHop) : 0;
            if (rHop > 0 && GameReflection.IsLiveHeroManager(rHm) == true)
            {
                if (rHop != HeroManagerOffset) Tunables.PinHeroManagerOffset(rHop);
                return rHm;
            }
            Base.LogEvent($"Reflection: TheHeroManager @0x{rHop:X} -> 0x{rHm:X8} not confirmed live — using content checks");
        }

        // Fast path: the pinned/default hop still points at an object that
        // verifiably CONTAINS the hero-array pair. Only the hero pair is
        // trusted here. A box-only match is NOT: the
        // HeroManagerTemplate one slot below carries equipment-archetype
        // arrays, and on 2026-09-26 a stale-default discovery pinned
        // +0xCF8 (the template) + ItemBoxOffset +0x294 (its archetype
        // array), after which this fast path accepted the template on
        // every scan. The template's roster is always empty, so a box-only
        // hit goes through the ranked discovery below instead.
        int off = HeroManagerOffset;
        int hm  = RdPtr(vpClient + off);
        if (IsGamePtr(hm) && IsHeroPairAt(hm, LocalHeroesOffset)) return hm;

        // Self-heal: a patch inserted fields into UDunDefViewportClient and
        // moved the hop. Rescan the window for a pointer whose target
        // passes the content gate; pin the winner.
        int found = DiscoverHeroManagerOffset(vpClient);
        if (found != 0)
        {
            if (found != off) Tunables.PinHeroManagerOffset(found);
            int winner = RdPtr(vpClient + found);
            // Usually we got here because the hero-pair offset is stale (the
            // hop itself rarely moves). Heal + pin it now so the next resolve
            // takes the fast path instead of re-ranking the window. Safe: a
            // pair is only found on the real manager.
            EnsureHeroPairOffset(winner);
            return winner;
        }
        // Nothing verifiable (menu/loading — arrays legitimately empty, or
        // a transient stale read). Return the raw hop like the original
        // code did; callers already run staged diagnosis on failure.
        return hm;
    }

    // An object that reads as THE live HeroManager: real UObject vtable +
    // verifiable content (the hero-array pair or the forge box at their
    // pinned/default offsets, else a hero pair anywhere in its window).
    // Content, not identity — an archetype/copy with empty arrays fails.
    // The discovery leg matters on a fresh install: until the hero offset
    // is pinned (or if the pin write fails), the pinned-offset legs fail on
    // the real manager too, and Item Dupe refused "inventory unavailable".
    // Read-only — nothing is pinned from here (the target may be the
    // template, which never has a hero pair).
    private static bool LooksLikeHeroManager(int hm)
    {
        if (!IsGamePtr(hm)) return false;
        uint vtable = (uint)RdInt(hm);
        if (vtable < 0x00400000u || vtable >= 0x02000000u) return false;
        return IsHeroPairAt(hm, LocalHeroesOffset) ||
               IsItemBoxAt(hm, ItemBoxOffset, ItemBoxFloor(LocalHeroesOffset)) ||
               DiscoverHeroArraysOffset(hm) != 0;
    }

    // Public form of the content gate. Callers need this to tell "the hop
    // resolved to the real manager" from "ResolveHeroManager fell back to the
    // raw pointer because nothing verified" — see the fallback at the end of
    // ResolveHeroManager, which deliberately returns an UNVERIFIED pointer so
    // staged diagnosis can still run. A non-zero HeroManager is therefore NOT
    // by itself evidence of success, and anything that reports success to the
    // user (CALIBRATE) has to check this too. The trap it guards against is
    // concrete: HeroManagerTemplate sits at vpClient+0xCF8, one slot below the
    // real hop, with the same vtable and empty arrays.
    public static bool IsVerifiedHeroManager(int hm) =>
        GameReflection.IsLiveHeroManager(hm) == true || LooksLikeHeroManager(hm);

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

    // RANKED, never first-hit. Scanning upward, HeroManagerTemplate
    // (+0xCF8) comes before the real hop (+0xCFC), shares its vtable, and
    // its archetype arrays can pass the box fingerprint — first-hit
    // discovery pinned it on 2026-09-26. Every candidate is scored and the
    // best wins: a verified hero pair beats any box-only match (the
    // template's roster is always empty), then the larger roster, then the
    // larger fingerprint-verified box (the real box is ~1000+ items, the
    // template's archetype array ~292).
    private static int DiscoverHeroManagerIn(int vpClient, int scanStart, int scanEnd)
    {
        if (!IsGamePtr(vpClient)) return 0;
        int winLen = scanEnd - scanStart + 4;
        byte[]? win;
        try { win = Base.Instance.ReadMemory(vpClient + scanStart, winLen); }
        catch { win = null; }
        if (win == null || win.Length < winLen) return 0;

        // Pass 1 — cheap: the hop moved but the hero-pair offset didn't.
        int bestOff = 0, bestHeroes = 0;
        for (int off = scanStart; off <= scanEnd; off += 4)
        {
            int hm = System.BitConverter.ToInt32(win, off - scanStart);
            if (!IsGamePtr(hm) || !IsHeroPairAt(hm, LocalHeroesOffset)) continue;
            int n = RdInt(hm + LocalHeroesOffset + 4);
            if (n > bestHeroes) { bestHeroes = n; bestOff = off; }
        }
        if (bestOff != 0)
        {
            Base.Log($"HeroMgr: hop at ViewportClient+0x{bestOff:X} (hero pair verified, {bestHeroes} heroes)");
            return bestOff;
        }

        // Pass 2 — deep: no in-play hero (menus) or the array offsets moved
        // too. Score every object by discovered hero roster, then by
        // fingerprint-verified box size.
        int bestBox = 0, runnerBox = 0;
        bestHeroes = 0; bestOff = 0;
        for (int off = scanStart; off <= scanEnd; off += 4)
        {
            int hm = System.BitConverter.ToInt32(win, off - scanStart);
            if (!IsGamePtr(hm)) continue;
            uint vtable = (uint)RdInt(hm);
            if (vtable < 0x00400000u || vtable >= 0x02000000u) continue;

            int h = DiscoverHeroArraysOffset(hm);
            int heroes = h != 0 ? RdInt(hm + h + 4) : 0;
            // Explicit floor: this candidate may be the template, so the
            // default (which heals + pins the hero offset) must not run.
            var box = DiscoverItemBox(hm, ItemBoxFloor(h != 0 ? h : LocalHeroesOffset));
            int boxNum = box.pairVerified ? box.count : 0;
            if (heroes == 0 && boxNum == 0) continue;

            Base.Log($"HeroMgr: candidate ViewportClient+0x{off:X} heroes={heroes} box={boxNum}");
            bool better = heroes != bestHeroes ? heroes > bestHeroes : boxNum > bestBox;
            if (better)
            {
                if (bestHeroes == 0 && heroes == 0) runnerBox = bestBox;
                bestHeroes = heroes; bestBox = boxNum; bestOff = off;
            }
            else if (heroes == 0 && bestHeroes == 0 && boxNum > runnerBox)
            {
                runnerBox = boxNum;
            }
        }
        // A box-only winner must beat every other box-only candidate
        // outright — a tie means we can't tell the real manager from the
        // template, and a wrong pin is sticky.
        if (bestOff != 0 && (bestHeroes > 0 || bestBox > runnerBox))
        {
            Base.Log($"HeroMgr: hop at ViewportClient+0x{bestOff:X} (deep-ranked: heroes={bestHeroes} box={bestBox})");
            return bestOff;
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
    // maxChars: 256 suits engine names; item text passes more (a name
    // colored letter by letter costs ~27 characters a letter).
    public static string ReadFString(int addr, int maxChars = 256)
    {
        try
        {
            int ptr = RdPtr(addr);
            int num = RdInt(addr + 4);
            if (!IsGamePtr(ptr) || num <= 1 || num > maxChars) return "";
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
    //
    // POSITION RULE (2026-09-26): the fingerprint alone is not unique. The
    // SDK declares UDunDefHeroManager's fields in the order LocalLoadedHeroes,
    // ActiveHeroes, ShopEquipments[3], ItemBoxEquipments, ItemBoxEntries —
    // so the box always sits ABOVE the hero-array pair. Patches insert
    // fields; they have never reordered them. Below the pair, at +0x294 on
    // the 2026-06 build, sits AdditionalReferences (SDK 0x288 + the +0xC
    // shift): 292 equipment archetypes followed by the non-equipment
    // MultiplayerModeImages array, i.e. a perfect fingerprint match. It was
    // pinned on 2026-09-26, and because a pinned offset that verifies is
    // trusted before any window scan, the forge then read archetypes on
    // every scan. Every box gate now requires off >= the pair base + 0x18.
    private const int HeroPairSize = 0x18; // LocalLoadedHeroes + ActiveHeroes

    private static int ItemBoxFloor(int heroPairOffset) => heroPairOffset + HeroPairSize;

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

        // Reflection first: the field NAMED ItemBoxEquipments, whatever its
        // items look like. This is the fix for heavily modded saves, whose
        // real box fails every "looks like a normal item" gate below.
        int rBox = GameReflection.FieldOffset(heroMgr, "ItemBoxEquipments");
        if (rBox > 0)
        {
            if (rBox != ItemBoxOffset) Tunables.PinItemBoxOffset(rBox);
            var byName = ReadPtrArray(heroMgr + rBox);
            Base.Log($"ItemBox: reflection +0x{rBox:X} -> {byName.Count} items");
            return byName;
        }

        // The box must sit above the hero pair (POSITION RULE above), so
        // locate the pair first — this also heals + pins a stale pair
        // offset, which is what makes the floor trustworthy.
        int floor = ItemBoxFloor(EnsureHeroPairOffset(heroMgr));

        // Fast path: the pinned/default offset still reads as a populated
        // equipment array carrying the ItemBoxEntries fingerprint, above
        // the hero pair → it is the box, no scan needed.
        int off = ItemBoxOffset;
        if (IsItemBoxAt(heroMgr, off, floor))
        {
            var fast = ReadPtrArray(heroMgr + off);
            Base.Log($"ItemBox: fast path OK at +0x{off:X} -> {fast.Count} items");
            return fast;
        }
        // Everything below is a self-heal attempt. Log the inputs, because
        // "the box is empty" and "the box moved" and "the pinned offset is
        // reading a shop page" all look identical from the outside.
        Base.Log($"ItemBox: pinned +0x{off:X} did NOT verify " +
                 $"(num={RdInt(heroMgr + off + 4)} entriesNum={RdInt(heroMgr + off + 0x10)}" +
                 (off < floor ? $", below the hero pair floor +0x{floor:X} — cannot be the box" : "") +
                 ") — rediscovering");

        // Pinned offset no longer positively reads as the box: a patch
        // moved it, the box is simply empty, or the fingerprint
        // assumption broke. Rediscover.
        (int found, bool verified, int count) = DiscoverItemBox(heroMgr, floor);
        // LogEvent, not Log: this single line is what identified the
        // edited-item box bug from a remote machine — it has to survive into
        // the shipped build's shareable log.
        Base.LogEvent($"ItemBox: discovery -> offset=+0x{found:X} verified={verified} count={count}");

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
        // A pinned offset below the floor is a known look-alike (the +0x294
        // archetype list): showing its contents as forge items is worse than
        // showing nothing, so it reads as empty.
        List<int> current = off >= floor ? ReadPtrArray(heroMgr + off) : new List<int>();
        if (found != 0 && found != off &&
            count >= ItemBoxLooseMinCount && count > current.Count * 4)
        {
            Base.Log($"ItemBox: loose candidate +0x{found:X} ({count} elems) used for display — not pinned (no ItemBoxEntries fingerprint)");
            return ReadPtrArray(heroMgr + found);
        }
        Base.Log($"ItemBox: FELL BACK to +0x{off:X} -> {current.Count} items " +
                 "(no fingerprint-verified box; an EMPTY box does this legitimately)");
        return current;
    }

    // Does this HeroManager offset read as THE box right now — a populated
    // equipment array with the ItemBoxEntries fingerprint next door, above
    // the hero pair (`floor` = pair base + 0x18)?
    private static bool IsItemBoxAt(int heroMgr, int off, int floor)
    {
        if (off < floor) return false;
        int data = RdPtr(heroMgr + off);
        int num  = RdInt(heroMgr + off + 4);
        int max  = RdInt(heroMgr + off + 8);
        if (!IsGamePtr(data) || num <= 0 || max < num || max > 200000) return false;
        if (!HasEntriesFingerprint(heroMgr, off)) return false;
        if (ElementsLookLikeEquipment(data, num)) return true;
        // Mirror the lenient discovery tier so an offset pinned by it isn't
        // re-discovered from scratch on every single scan. No size floor
        // here: this offset is already trusted, and a SMALL box full of
        // edited items (a low inventory on a modded save) would otherwise
        // fail every scan and pay a window sweep each time.
        return ElementsLookLikeEquipment(data, num, lenient: true);
    }

    // Scan the HeroManager window for the ItemBoxEquipments TArray.
    // Returns the best candidate offset (0 = none), whether it carries the
    // ItemBoxEntries fingerprint (only then may it be pinned), and its
    // element count. Read-only; needs a populated box to succeed — an
    // empty box keeps the current offset. Candidates below `floor` (the hero
    // pair base + 0x18, POSITION RULE above) are never considered; the
    // default locates the pair on this HeroManager (healing + pinning a
    // stale pair offset). Callers probing an object that may NOT be the real
    // manager must pass an explicit floor so nothing is pinned from it.
    public static (int offset, bool pairVerified, int count) DiscoverItemBox(int heroMgr, int floor = -1)
    {
        if (floor < 0)
        {
            // Called on the real manager: the named field is authoritative.
            int rBox = GameReflection.FieldOffset(heroMgr, "ItemBoxEquipments");
            if (rBox > 0) return (rBox, true, System.Math.Max(0, RdInt(heroMgr + rBox + 4)));
            floor = ItemBoxFloor(EnsureHeroPairOffset(heroMgr));
        }
        var r = DiscoverItemBoxIn(heroMgr, ItemBoxScanStart, ItemBoxScanEnd, false, floor);
        if (r.pairVerified) return r;
        var w = DiscoverItemBoxIn(heroMgr, ItemBoxScanWideStart, ItemBoxScanWideEnd, false, floor);
        if (w.pairVerified) return w;

        // ── Fallback tier: EDITED-ITEM saves (added 2026-09-04) ──
        // Nothing passed the vanilla value ranges. A real user's 2336-item
        // box was invisible here while the 3-item shop pages beside it
        // passed, so the forge silently read a shop page instead. Retry with
        // the value ranges dropped (structure still fully enforced), and buy
        // the looseness back three ways: near-unanimous element agreement
        // (90 %), the ItemBoxEntries fingerprint still REQUIRED to pin, and a
        // size floor so a small look-alike can never win this tier. Strict
        // wins whenever it can, so working saves never reach this code and
        // the documented +0x294 near-miss stays rejected there.
        // Known offset FIRST, with no size floor. The window scan below
        // keeps the largest fingerprint-verified candidate, which assumes the
        // real box is the biggest equipment-shaped array around — true for a
        // 2336-item box, false for a 12-item box on a modded save, which
        // would lose the pin to a bigger look-alike (the documented
        // 292-element array at +0x294). If the pinned/default offset itself
        // passes the lenient gate + fingerprint + floor, that is the answer.
        if (IsItemBoxAt(heroMgr, ItemBoxOffset, floor))
        {
            int pinnedCount = RdInt(heroMgr + ItemBoxOffset + 4);
            Base.Log($"ItemBox: LENIENT tier — pinned +0x{ItemBoxOffset:X} verifies ({pinnedCount} items); keeping it");
            return (ItemBoxOffset, true, pinnedCount);
        }
        var lr = DiscoverItemBoxIn(heroMgr, ItemBoxScanStart, ItemBoxScanEnd, true, floor);
        if (lr.pairVerified && lr.count >= ItemBoxLooseMinCount)
        {
            Base.Log($"ItemBox: LENIENT tier matched +0x{lr.offset:X} ({lr.count} items) — " +
                     "strict value ranges rejected every candidate (edited items?)");
            return lr;
        }
        var lw = DiscoverItemBoxIn(heroMgr, ItemBoxScanWideStart, ItemBoxScanWideEnd, true, floor);
        if (lw.pairVerified && lw.count >= ItemBoxLooseMinCount)
        {
            Base.Log($"ItemBox: LENIENT tier matched +0x{lw.offset:X} ({lw.count} items, wide window)");
            return lw;
        }
        return w.count > r.count ? w : r;
    }

    private static (int offset, bool pairVerified, int count) DiscoverItemBoxIn(
        int heroMgr, int scanStart, int scanEnd, bool lenient, int floor)
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
            if (off < floor) continue; // below the hero pair — never the box
            int i    = off - scanStart;
            int data = System.BitConverter.ToInt32(win, i);
            int num  = System.BitConverter.ToInt32(win, i + 4);
            int max  = System.BitConverter.ToInt32(win, i + 8);
            // Plausible populated TArray header (UE3 invariant: Num ≤ Max).
            if (!IsGamePtr(data) || num <= 0 || num > 200000 || max < num || max > 200000)
                continue;
            if (!ElementsLookLikeEquipment(data, num, lenient)) continue;

            // Fingerprint: the next field is a valid populated NON-equipment
            // TArray (ItemBoxEntries — per-user entries, Num ≥ 1). A shop
            // page fails it: its neighbour is another equipment array (the
            // next page, or the box itself). Live-verified 2026-07-02: box
            // num=1114 followed by entries num=1; num-equality does NOT
            // hold and must not be required.
            int pData = System.BitConverter.ToInt32(win, i + 0xC);
            int pNum  = System.BitConverter.ToInt32(win, i + 0x10);
            int pMax  = System.BitConverter.ToInt32(win, i + 0x14);
            // NotEquipment, not !Equipment: an unreadable neighbour must not
            // count as "the entries array is next door" (see ClassifyArray).
            // Neighbour judged with the LENIENT gate, always (2026-09-26): a
            // strict "not equipment" verdict on a box of heavily MODDED items
            // made the 3-item shop page just below it look fingerprinted, and
            // the forge read the shop. ItemBoxEntries (FItemBoxEntry structs)
            // is not equipment under either gate.
            bool pair = IsGamePtr(pData) && pNum > 0 && pMax >= pNum && pMax <= 200000 &&
                        ClassifyArray(pData, pNum, lenient: true) == ArrayKind.NotEquipment;

            Base.Log($"ItemBox: candidate HeroManager+0x{off:X} num={num} pair={pair}" +
                     (lenient ? " [lenient]" : ""));
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
    // are per-user (live-verified Num=1 against a large item box). The
    // neighbour is judged with the LENIENT gate — a strict gate calls a box
    // of heavily modded items "not equipment", which fingerprinted the
    // 3-item shop page below it (2026-09-26).
    private static bool HasEntriesFingerprint(int heroMgr, int off)
    {
        int pData = RdPtr(heroMgr + off + 0xC);
        int pNum  = RdInt(heroMgr + off + 0x10);
        int pMax  = RdInt(heroMgr + off + 0x14);
        return IsGamePtr(pData) && pNum > 0 && pMax >= pNum && pMax <= 200000 &&
               ClassifyArray(pData, pNum, lenient: true) == ArrayKind.NotEquipment;
    }

    // Do this array's elements read as live UHeroEquipment*?
    // Requires ≥ItemBoxMinScore hits (capped at Num) AND ≥75 % of the
    // sample, so stride-aliased struct arrays don't sneak past.
    //
    // The sample is SPREAD ACROSS THE WHOLE ARRAY, not taken from the front
    // (fixed 2026-09-04). Sampling elements 0..7 of a 2336-item box judges
    // the entire box by whatever happens to sit in its first eight slots —
    // and a real user's report had exactly that: the box at +0x3A8 (2336
    // items) was rejected outright while the 3-item shop pages beside it
    // passed, so the forge fell back to reading a shop page. Reading the
    // array end-to-end is the same cost per sample and immune to a local
    // cluster of odd items.
    private static bool ElementsLookLikeEquipment(int dataPtr, int num)
        => ClassifyArray(dataPtr, num, lenient: false) == ArrayKind.Equipment;

    private static bool ElementsLookLikeEquipment(int dataPtr, int num, bool lenient)
        => ClassifyArray(dataPtr, num, lenient) == ArrayKind.Equipment;

    // Three outcomes, and the third one matters. The item-box fingerprint asks
    // whether the NEIGHBOUR array is *not* equipment — so if a failed read
    // collapses into "not equipment", a transient read race becomes positive
    // evidence and can promote a look-alike into the verified tier, which is
    // then PINNED to overrides.json. Only a read that completed and then
    // failed the gate is evidence of anything; "couldn't read it" is not.
    private enum ArrayKind { Unreadable, Equipment, NotEquipment }

    private static ArrayKind ClassifyArray(int dataPtr, int num, bool lenient)
    {
        if (!IsGamePtr(dataPtr) || num <= 0) return ArrayKind.Unreadable;
        int sample = num < ItemBoxSampleCount ? num : ItemBoxSampleCount;
        byte[]? arr;
        try { arr = Base.Instance.ReadMemory(dataPtr, num * 4); }
        catch { return ArrayKind.Unreadable; }
        if (arr == null || arr.Length < num * 4) return ArrayKind.Unreadable;

        int hits = 0;
        for (int s = 0; s < sample; s++)
        {
            // Even spread: first, last, and evenly spaced in between.
            int i = sample == 1 ? 0 : (int)((long)s * (num - 1) / (sample - 1));
            if (LooksLikeEquipment(System.BitConverter.ToInt32(arr, i * 4), lenient)) hits++;
        }
        int needed = ItemBoxMinScore < sample ? ItemBoxMinScore : sample;
        // Lenient mode drops the vanilla value ranges, so it has to buy that
        // back with near-unanimity (90 % vs 75 %) — see DiscoverItemBox.
        bool ok = hits >= needed &&
                  (lenient ? hits * 10 >= sample * 9 : hits * 4 >= sample * 3);
        // A big array that ALMOST passes is the interesting failure — that is
        // a real box being rejected. Log it so a remote report shows it.
        if (!ok && !lenient && num >= ItemBoxLooseMinCount)
            Base.Log($"ItemBox: array of {num} REJECTED by the strict equipment gate ({hits}/{sample} passed)");
        return ok ? ArrayKind.Equipment : ArrayKind.NotEquipment;
    }

    private static readonly int ItemNativeSize =
        System.Runtime.InteropServices.Marshal.SizeOf<ItemNative>();

    // A UHeroEquipment*: a real UObject (code-section vtable) whose inline
    // ItemNative (he + 0x38) passes the same sanity gates the forge's
    // bootstrap trusts for a genuine item. Strong enough that a hero object
    // or a garbage pointer landing here at a wrong offset scores ~0.
    private static bool LooksLikeEquipment(int he) => LooksLikeEquipment(he, lenient: false);

    // lenient: keep every STRUCTURAL check, drop the vanilla VALUE ranges.
    // Only used by the fallback discovery tier (see DiscoverItemBox) for
    // saves full of edited items, where the vanilla ranges reject a real box.
    private static bool LooksLikeEquipment(int he, bool lenient)
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
        // Level bounds are a PLAUSIBILITY heuristic, not structure. Vanilla
        // items sit well under 500; EDITED items (by this very tool, or
        // traded in from someone who did) routinely don't, and in lenient
        // mode the only job left is to stay below the heap-pointer floor
        // (0x01000000 = 16.7M) so a pointer-shaped dword can't pass as a
        // level. The real rejection power is structural — code-section
        // vtable, aligned EquipmentTemplate, consistent name header —
        // reinforced by the percentage rule at the call site.
        int lvlCap = lenient ? 1_000_000 : 500;
        if (it.Level < 0 || it.Level > lvlCap) return false;
        if (it.MaxEquipmentLevel <= 0 || it.MaxEquipmentLevel > lvlCap) return false;
        // Max >= Level is a vanilla INVARIANT, not a structural one: an
        // edited item can carry Level above its own cap.
        if (!lenient && it.MaxEquipmentLevel < it.Level) return false;
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
        int off = EnsureHeroPairOffset(heroMgr);
        var result = activeOnly ? ReadPtrArray(heroMgr + off + 0xC)
                                : ReadPtrArray(heroMgr + off, 8);
        Base.Log($"HeroArrays: read {(activeOnly ? "active" : "local")} at +0x{(activeOnly ? off + 0xC : off):X} " +
                 $"-> {result.Count} heroes");
        return result;
    }

    // The hero-pair base for THIS HeroManager: the pinned offset while it
    // verifies, else rediscovered and pinned. Falls back to the pinned value
    // when nothing verifies, so callers always get a usable offset. Also the
    // item box's floor (POSITION RULE), which is why the box path calls it.
    // Only call on the real manager — a found pair is pinned.
    private static int EnsureHeroPairOffset(int heroMgr)
    {
        // Reflection first. The code reads ActiveHeroes as the pair base +
        // 0xC, so only accept the named offsets while that still holds.
        int rLocal = GameReflection.FieldOffset(heroMgr, "LocalLoadedHeroes");
        if (rLocal > 0 && GameReflection.FieldOffset(heroMgr, "ActiveHeroes") == rLocal + 0xC)
        {
            if (rLocal != LocalHeroesOffset) Tunables.PinLocalHeroesOffset(rLocal);
            return rLocal;
        }
        int off = LocalHeroesOffset;
        if (IsHeroPairAt(heroMgr, off)) return off;
        Base.Log($"HeroArrays: pinned +0x{off:X} did NOT verify " +
                 $"(local num={RdInt(heroMgr + off + 4)} active num={RdInt(heroMgr + off + 0x10)}) — rediscovering");
        int found = DiscoverHeroArraysOffset(heroMgr);
        Base.Log($"HeroArrays: discovery -> offset=+0x{found:X}" +
                 (found == 0 ? " (NOTHING verified — the dense-roster + sparse-active pair test failed)" : ""));
        // Nothing verified: keep the pinned/default offset — empty menus and
        // transient stale reads must not degrade a good pin.
        if (found == 0) return off;
        if (found != off) Tunables.PinLocalHeroesOffset(found);
        return found;
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
        // The hero level cap is 100, so 1000 is already 10x headroom. Kept
        // tight on purpose: a wider bound buys nothing real and weakens the
        // gate against freed / reused memory.
        if (level < 0 || level > 1000) return false;
        if (cap <= 0 || cap > 10000) return false;

        int nPtr = System.BitConverter.ToInt32(d, 0x60);   // hero+0x564 HeroName FString
        int nLen = System.BitConverter.ToInt32(d, 0x64);
        int nMax = System.BitConverter.ToInt32(d, 0x68);
        if (nPtr == 0) return nLen == 0 && nMax == 0;
        if (!IsGamePtr(nPtr)) return false;
        return nLen >= 0 && nLen <= 4096 && nMax >= 0 && nMax <= 4096;
    }

    // ── Diagnostic: what do this save's arrays actually look like? ──
    //
    // The counts at the PINNED offsets only say "we read N items there". When
    // a save behaves differently from a known-good one, the question is what
    // is at every OTHER candidate offset — is the box somewhere else, is it
    // empty, does the ItemBoxEntries neighbour exist, do the hero arrays have
    // the dense+sparse shape discovery requires? This dumps the whole
    // HeroManager window through the same gates discovery uses, so a remote
    // report shows the real layout instead of a verdict. Read-only; the exact
    // shape of data that solved the 2026-07-02 shop-page bug.
    internal static string DescribeArrayWindow(int heroMgr)
    {
        var sb = new System.Text.StringBuilder();
        if (!IsGamePtr(heroMgr)) return "  (no HeroManager)\n";
        const int start = 0x280, end = 0x480;
        // Read-only: locate the hero pair without pinning (the report must
        // never change state), then apply the same floor discovery uses.
        int pair = IsHeroPairAt(heroMgr, LocalHeroesOffset) ? LocalHeroesOffset
                 : DiscoverHeroArraysOffset(heroMgr);
        int floor = ItemBoxFloor(pair != 0 ? pair : LocalHeroesOffset);
        int hits = 0;
        for (int off = start; off <= end; off += 4)
        {
            int data = RdPtr(heroMgr + off);
            int num  = RdInt(heroMgr + off + 4);
            int max  = RdInt(heroMgr + off + 8);
            // Same plausible-TArray-header gate as discovery (UE3 Num <= Max).
            if (!IsGamePtr(data) || num <= 0 || num > 200000 || max < num || max > 200000) continue;

            bool eq    = ElementsLookLikeEquipment(data, num);
            bool dense = ElementsLookLikeHeroes(data, num, 8) || ElementsLookLikeHeroes(data, num, 4);
            // ActiveHeroes is a SPARSE slot array (live: 40 slots, one hero),
            // so the dense >=75% rule never matches it — but it is the second
            // half of the pair discovery requires, so it has to be visible
            // here or a failed hero discovery is unreadable.
            bool sparse = !dense && SparseSlotsLookLikeHeroes(data, num);
            // The box's tell: next field is a populated NON-equipment TArray.
            int pData = RdPtr(heroMgr + off + 0xC);
            int pNum  = RdInt(heroMgr + off + 0x10);
            bool entriesNext = IsGamePtr(pData) && pNum > 0 && !ElementsLookLikeEquipment(pData, pNum);

            string tag = eq ? (!entriesNext ? "equipment (shop page / lobby?)"
                               : off < floor ? "equipment +entries-next, BELOW hero pair => not the box (archetype list)"
                               : "EQUIPMENT +entries-next => ITEM BOX")
                       : dense ? "heroes (dense => LocalLoadedHeroes)"
                       : sparse ? "heroes (sparse => ActiveHeroes)"
                       : "other";
            sb.Append($"    +0x{off:X3} num={num,-6} max={max,-6} {tag}\n");
            if (++hits >= 40) { sb.Append("    ...truncated\n"); break; }
        }
        if (hits == 0) sb.Append("    (no TArray-shaped fields found in the window — chain target may be wrong)\n");
        return sb.ToString();
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
            return "No character found — the game looks like it's in a menu or loading screen " +
                   "(and the game's object list didn't expose the item manager: " + GameReflection.ObjectListStatus + "). " +
                   "Go to the Tavern or a mission, then rescan. " +
                   "If that doesn't help, run CALIBRATE in Settings.";
        // The pawn resolved but the chain off it didn't. The common causes
        // are ordered by how often they actually bite: a transient read right
        // after a map change, or the tool locking onto the wrong character
        // (an online game where another player's pawn was picked — the same
        // fault also makes Unlimited Mana stop working while Auto-Kill and
        // Max Tower Units keep going, because those never use the character).
        return "Found your character but couldn't reach the item manager from it. " +
               "If you're in an online game, this tool only works reliably in solo/local play — " +
               "try a solo game. Otherwise rescan in a few seconds (common right after a map change), " +
               "or run CALIBRATE in Settings. Settings → Diagnostics → COPY REPORT shows exactly " +
               "which link is broken.";
    }
}
