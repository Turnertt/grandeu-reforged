using System.Collections.Generic;
using System.Linq;

namespace Modinator;

// Verifies — never discovers — every hardcoded (class, field, offset[, mask])
// the tool uses, by name from the game's own UProperty table, once per
// attach, on a worker. Each row gets a verdict:
//   Match   — the game declares the field where the tool expects it;
//   Moved   — the game declares it somewhere else (Actual says where);
//   Unknown — couldn't check (reflection waiting / off, class not loaded,
//             no property by that name). Never treated as Match or Moved:
//             every consumer keeps its pre-registry behavior on Unknown.
// Why (2026-10-29): the 10/23 DD1 patch grew DunDefDroppedEquipment by 0xC
// and Floor listed nothing until a scratch-harness sweep said which field
// moved. This ships that sweep. Verdicts are not persisted (DECISIONS
// 2026-09-26: trusted stored values were the pin-class bugs).
//
// Consumers: COPY REPORT (`layout :`), the item/hero block write gate
// (ItemBlockOk / HeroBlockOk), and Auto-Kill's once-per-attach layout
// (MainWindow.AdoptAkLayout). Classes are found by package-qualified name
// (GameReflection.ClassByName), so the whole table is checked at the menu.
internal static class LayoutRegistry
{
    internal enum Kind { Match, Moved, Unknown }
    internal enum Block { None, Item, Hero }

    // BlockOffset: the field's offset inside ItemNative / HeroNative (-1 = not a block row).
    internal readonly record struct Row(string Package, string Class, string Field, int Expected, uint Mask,
        string Consumer, Block Block = Block.None, int BlockOffset = -1, string ToolName = "");

    internal readonly record struct Verdict(Kind Kind, int Actual, uint ActualMask, string Reason)
    {
        public bool IsMoved => Kind == Kind.Moved;
    }

    private const int ItemBase = 0x38;  // ItemNative starts at UObject.ObjectArchetype
    private const int HeroBase = 0x504; // HeroNative starts at UDunDefHero.HeroHealthModifier

    // ── The table ──────────────────────────────────────────────────────
    // Expected values: live by name on build 0x6ADBB522 (harness S/L,
    // 2026-10-29). "(by name)" = the consumer already resolves the field by
    // name and follows a move; "(fixed)" = it uses the literal.
    private static readonly Row[] ChainRows =
    {
        // Engine (frozen engine package — reported, Auto-Kill pauses on a move)
        new("Engine", "Actor", "WorldInfo", 0x110, 0, "Floor (by name); Auto-Kill pawn check (fixed)"),
        new("Engine", "Actor", "LifeSpan", 0x114, 0, "Auto-Kill, enemy towers (fixed)"),
        new("Engine", "Actor", "bHidden", 0xB0, 0x4, "Floor (by name)"),
        new("Engine", "Actor", "bDeleteMe", 0xB0, 0x10, "Floor (by name)"),
        new("Engine", "Pawn", "Controller", 0x22C, 0, "player chain, Mana (fixed)"),
        new("Engine", "Pawn", "NextPawn", 0x230, 0, "Auto-Kill pawn walk (fixed)"),
        new("Engine", "Pawn", "PlayerReplicationInfo", 0x38C, 0, "Auto-Kill hero protection, player pick (fixed)"),
        new("Engine", "Pawn", "Health", 0x324, 0, "Auto-Kill (fixed)"),
        new("Engine", "Pawn", "HealthMax", 0x328, 0, "Auto-Kill (fixed)"),
        new("Engine", "WorldInfo", "PawnList", 0x41C, 0, "Auto-Kill pawn walk (fixed)"),
        new("Engine", "WorldInfo", "TimeDilation", 0x374, 0, "game speed, world check (fixed)"),
        new("Engine", "WorldInfo", "Game", 0x3FC, 0, "enemy towers (fixed)"),
        new("Engine", "WorldInfo", "GRI", 0x3D0, 0, "level flags, Max Tower Units (fixed); multiplayer (by name)"),
        new("Engine", "GameReplicationInfo", "PRIArray", 0x264, 0, "multiplayer picker (by name)"),
        new("Engine", "PlayerController", "Player", 0x3B8, 0, "player chain (fixed)"),
        new("Engine", "LocalPlayer", "ViewportClient", 0x194, 0, "player chain (fixed)"),
        new("Engine", "PlayerReplicationInfo", "PlayerName", 0x230, 0, "multiplayer picker (by name)"),
        // Game classes (UDKGame — these are what DD1 patches move)
        new("UDKGame", "DunDefPlayerController", "ManaPower", 0x6B4, 0, "Mana (by name per attach)"),
        new("UDKGame", "DunDefPlayerController", "MaxManaPower", 0x6B8, 0, "Mana (by name per attach)"),
        new("UDKGame", "DunDefGameReplicationInfo", "MaxTowerUnits", 0x39C, 0, "Max Tower Units (by name per attach)"),
        new("UDKGame", "DunDefGameReplicationInfo", "IsLobbyLevel", 0x2C4, 0x40000, "Auto-Kill level check (by name)"),
        new("UDKGame", "DunDefGameReplicationInfo", "IsGameplayLevel", 0x2C4, 0x80000, "Auto-Kill level check (by name)"),
        new("UDKGame", "Main", "TargetableActors", 0x394, 0, "enemy towers (by name per attach)"),
        new("UDKGame", "DunDefDamageableTarget", "TargetingTeam", 0x2CC, 0, "enemy towers (by name per attach)"),
        new("UDKGame", "DunDefViewportClient", "TheHeroManager", 0xCFC, 0, "Forge / Hero chain (by name)"),
        new("UDKGame", "DunDefHeroManager", "ItemFolders", 0x98, 0, "Forge folders (by name)"),
        new("UDKGame", "DunDefHeroManager", "LocalLoadedHeroes", 0x36C, 0, "Hero / Forge (by name)"),
        new("UDKGame", "DunDefHeroManager", "ActiveHeroes", 0x378, 0, "Hero / Forge (by name)"),
        new("UDKGame", "DunDefHeroManager", "ItemBoxEquipments", 0x3A8, 0, "Forge item box (by name)"),
        new("UDKGame", "DunDefHeroManager", "ItemBoxEntries", 0x3B4, 0, "item box fingerprint (relative to the box)"),
        new("UDKGame", "DunDefHero", "HeroClassDisplayName", 0x424, 0, "Hero cards (by name)"),
        new("UDKGame", "DunDefHero", "ClassNameColor", 0x444, 0, "Hero cards (by name)"),
        new("UDKGame", "DunDefHero", "HeroEquipments", 0x5B0, 0, "Forge / Hero / Dupe / multiplayer (by name)"),
        new("UDKGame", "DunDefHero", "HeroWeaponEquipment", 0x5C8, 0, "Hero / Dupe / multiplayer (by name)"),
        new("UDKGame", "DunDefHero", "BasedOnHeroTemplate", 0x5D8, 0, "Hero cards (by name)"),
        new("UDKGame", "DunDefPlayerReplicationInfo", "myHero", 0x358, 0, "multiplayer picker (by name)"),
        new("UDKGame", "DunDefDroppedEquipment", "MyEquipmentObject", 0x324, 0, "Floor (by name)"),
        new("UDKGame", "DunDefDroppedEquipment", "DoFadeOut", 0x314, 0x8, "Floor (by name)"),
        new("UDKGame", "DunDefDroppedEquipment", "bTransferredToItemBox", 0x314, 0x10, "Floor (by name)"),
        new("UDKGame", "DunDefDroppedEquipment", "bAutoDestroyed", 0x314, 0x20, "Floor (by name)"),
        new("UDKGame", "HeroEquipment", "weaponType", 0x824, 0, "MAX weapon gate, dupe ranking (by name)"),
        new("UDKGame", "HeroEquipment", "RandomBaseNames", 0xA3C, 0, "item names (by name)"),
        new("UDKGame", "HeroEquipment", "AllowNameRandomization", 0x3E0, 0x80000, "item names (by name)"),
    };

    // ItemNative field → the game property at that position (DD1_INTERNALS
    // §8b "ItemNative is correct"). (tool field, byte delta into it, game
    // property, bool mask). The two pad bytes after R2[2] have no property.
    private static readonly (string tool, int delta, string game, uint mask)[] ItemMap =
    {
        ("EquipmentTemplate", 0, "ObjectArchetype", 0), ("R0", 0, "StatModifiers", 0),
        ("DamageReductions", 0, "DamageReductions", 0),
        ("WeaponDamageBonus", 0, "WeaponDamageBonus", 0), ("WeaponNumberOfProjectilesBonus", 0, "WeaponNumberOfProjectilesBonus", 0),
        ("WeaponSpeedOfProjectilesBonus", 0, "WeaponSpeedOfProjectilesBonus", 0),
        ("WeaponAdditionalDamage", 0, "WeaponAdditionalDamageType", 0), ("WeaponAdditionalDamage", 4, "WeaponAdditionalDamageAmount", 0),
        ("WeaponDrawScaleMultiplier", 0, "WeaponDrawScaleMultiplier", 0),
        ("MaxRandomElementalDamageMultiplier", 0, "MaxRandomElementalDamageMultiplier", 0),
        ("WeaponSwingSpeedMultiplier", 0, "WeaponSwingSpeedMultiplier", 0),
        ("Flags", 0, "bCanBeUpgraded", 0x8), ("Flags", 0, "bIsLocked", 0x8000),
        ("WeaponReloadSpeedBonus", 0, "WeaponReloadSpeedBonus", 0), ("WeaponKnockbackBonus", 0, "WeaponKnockbackBonus", 0),
        ("WeaponAltDamageBonus", 0, "WeaponAltDamageBonus", 0), ("WeaponBlockingBonus", 0, "WeaponBlockingBonus", 0),
        ("WeaponClipAmmoBonus", 0, "WeaponClipAmmoBonus", 0),
        ("AdditionalAllowedUpgradeResistancePoints", 0, "AdditionalAllowedUpgradeResistancePoints", 0),
        ("RequirementLevelOverride", 0, "RequirementLevelOverride", 0), ("WeaponChargeSpeedBonus", 0, "WeaponChargeSpeedBonus", 0),
        ("WeaponShotsPerSecondBonus", 0, "WeaponShotsPerSecondBonus", 0),
        ("NameIndex_Base", 0, "NameIndex_Base", 0), ("NameIndex_QualityDescriptor", 0, "NameIndex_QualityDescriptor", 0),
        ("NameIndex_DamageReduction", 0, "NameIndex_DamageReduction", 0),
        ("PrimaryColorSet", 0, "PrimaryColorSet", 0), ("SecondaryColorSet", 0, "SecondaryColorSet", 0),
        ("R1", 0, "bCantBeDropped", 0), ("R1", 1, "bCantBeSold", 0), ("R1", 2, "bAutoLockInItemBox", 0),
        ("Mystery", 0, "bDidOnetimeEffect", 0), ("ManualLR", 0, "ManualLR", 0), ("EquipmentType", 0, "EquipmentType", 0),
        ("R2", 0, "bHideQualityDescriptors", 0), ("R2", 1, "bEquipmentFeatureByte1", 0), ("R2", 2, "bEquipmentFeatureByte2", 0),
        ("PrimaryColorSets", 0, "PrimaryColorSets", 0), ("SecondaryColorSets", 0, "SecondaryColorSets", 0),
        ("PrimaryColorOverride", 0, "PrimaryColorOverride", 0), ("SecondaryColorOverride", 0, "SecondaryColorOverride", 0),
        ("MaximumSellWorth", 0, "MaximumSellWorth", 0), ("MinimumSellWorth", 0, "MinimumSellWorth", 0),
        ("ShopMinimumSellWorth", 0, "ShopMinimumSellWorth", 0), ("MaxEquipmentLevel", 0, "MaxEquipmentLevel", 0),
        ("EquipmentName", 0, "UserEquipmentName", 0), ("Description", 0, "EquipmentDescription", 0),
        ("_InstancePad", 0, "StatEquipmentIDs", 0), ("_InstancePad", 40, "StatEquipmentTiers", 0),
        ("_InstancePad", 80, "QualityBeamColorOverride", 0), ("_InstancePad", 96, "FeatureArray", 0),
        ("_InstancePad", 136, "EquipmentTimeStamp", 0), ("_InstancePad", 148, "EquipmentFeatureString", 0),
        ("_InstancePad", 160, "equipmentTemplateUniqueID", 0),
        ("ForgerName", 0, "UserForgerName", 0), ("DroppedLocation", 0, "DroppedLocation", 0), ("FolderID", 0, "FolderID", 0),
        ("Level", 0, "Level", 0), ("StoredMana", 0, "StoredMana", 0), ("UserID", 0, "UserID", 0),
        ("MyRatingPercent", 0, "MyRatingPercent", 0), ("MyRating", 0, "MyRating", 0),
        ("EquipmentID1", 0, "EquipmentID1", 0), ("EquipmentID2", 0, "EquipmentID2", 0),
        ("BaseEquipmentName", 0, "EquipmentName", 0),
    };

    // HeroNative field → UDunDefHero property (DD1_INTERNALS §8b: the block
    // "matches field-for-field"). The ten StatModifiers are ten separate ints.
    private static readonly (string tool, int delta, string game)[] HeroMap =
    {
        ("StatModifiers", 0x00, "HeroHealthModifier"), ("StatModifiers", 0x04, "HeroSpeedModifier"),
        ("StatModifiers", 0x08, "HeroDamageModifier"), ("StatModifiers", 0x0C, "HeroCastingModifier"),
        ("StatModifiers", 0x10, "HeroAbilityOneModifier"), ("StatModifiers", 0x14, "HeroAbilityTwoModifier"),
        ("StatModifiers", 0x18, "HeroDefenseHealthModifier"), ("StatModifiers", 0x1C, "HeroDefenseAttackRateModifier"),
        ("StatModifiers", 0x20, "HeroDefenseDamageModifier"), ("StatModifiers", 0x24, "HeroDefenseAreaOfEffectModifier"),
        ("Level", 0, "HeroLevel"), ("MaxLevel", 0, "HeroLevelCap"), ("MaxDemoLevel", 0, "HeroLevelCapDemo"),
        ("Experience", 0, "HeroExperience"),
        ("R1", 0, "ManaPower"), ("R1", 4, "LastPawnHealth"), ("R1", 8, "MyHeroIconMatInst"),
        ("R1", 12, "MyTalkingHeroIconMatInst"), ("R1", 16, "MyTalkingIconMatInst"), ("R1", 20, "ColorableMiniMapIconMatInst"),
        ("R1", 24, "GUID1"), ("R1", 28, "GUID2"), ("R1", 32, "GUID3"), ("R1", 36, "GUID4"),
        ("HeroName", 0, "HeroName"), ("R2", 0, "colorTemplates"),
        ("Color1", 0, "color1"), ("Color2", 0, "color2"), ("Color3", 0, "color3"),
    };

    // Instance sizes (UStruct.PropertySize) on build 0x6ADBB522, harness P
    // 2026-10-29. Report only: a changed size says "this class grew" even
    // when no field the tool reads moved.
    private static readonly (string package, string cls, int size)[] ClassSizes =
    {
        ("Core", "Object", 0x3C), ("Engine", "Actor", 0x21C), ("Engine", "Pawn", 0x488), ("Engine", "WorldInfo", 0x738),
        ("Engine", "GameReplicationInfo", 0x27C), ("Engine", "PlayerController", 0x610), ("Engine", "LocalPlayer", 0x3D0),
        ("Engine", "PlayerReplicationInfo", 0x2D4), ("UDKGame", "DunDefPlayerController", 0xB4C),
        ("UDKGame", "DunDefGameReplicationInfo", 0x77C), ("UDKGame", "Main", 0x59C), ("UDKGame", "DunDefDamageableTarget", 0x514),
        ("UDKGame", "DunDefViewportClient", 0xE2C), ("UDKGame", "DunDefHeroManager", 0x710), ("UDKGame", "DunDefHero", 0x600),
        ("UDKGame", "DunDefPlayerReplicationInfo", 0x370), ("UDKGame", "DunDefDroppedEquipment", 0x51C),
        ("UDKGame", "HeroEquipmentNative", 0x21C), ("UDKGame", "HeroEquipment", 0xAF8),
    };

    public static readonly Row[] Rows = BuildRows();

    private static Row[] BuildRows()
    {
        var rows = new List<Row>(ChainRows);
        foreach (var (tool, delta, game, mask) in ItemMap)
        {
            int at = Base.OffsetOf<ItemNative>(tool) + delta;
            rows.Add(new("UDKGame", "HeroEquipment", game, ItemBase + at, mask, "Item Edit / Bulk Edit / Dupe / MAX (fixed block)",
                         Block.Item, at, delta == 0 ? tool : $"{tool}+{delta}"));
        }
        foreach (var (tool, delta, game) in HeroMap)
        {
            int at = Base.OffsetOf<HeroNative>(tool) + delta;
            rows.Add(new("UDKGame", "DunDefHero", game, HeroBase + at, 0, "Hero Edit (fixed block)",
                         Block.Hero, at, delta == 0 ? tool : $"{tool}+0x{delta:X}"));
        }
        return rows.ToArray();
    }

    // ── State (per attach) ─────────────────────────────────────────────
    // _gate guards the published state below and is only ever held for
    // dictionary work, so the lookups stay UI-safe. _runGate serializes the
    // check itself (object walk + ~136 property-chain walks, ~0.4 s cold),
    // which runs with _gate released.
    private static readonly object _gate = new();
    private static readonly object _runGate = new();
    private static (int pid, long gen) _session;
    private static bool _decided;   // every row has a final verdict for this attach
    private static long _retryAt;   // transient Unknowns (waiting / torn) retry no sooner than this
    private static int _attempts;   // checks run this attach (bounds the "class not loaded" retries)
    private static string _logged = "";
    private static readonly Dictionary<(string cls, string field), Verdict> _verdicts = new();
    private static readonly Dictionary<string, int> _liveSizes = new(); // class leaf -> live PropertySize
    private static string _summary = "not checked yet", _report = "not checked yet";

    public static string Summary { get { lock (_gate) return _summary; } }
    public static string Report { get { lock (_gate) return _report; } }
    public static bool Decided { get { lock (_gate) return Same() && _decided; } }

    private static bool Same() => _session == (Base.AttachedPid, Base.Instance.AttachmentGeneration);

    // Run the check if this attach hasn't been decided. Call from a WORKER
    // (Forge / Hero scan, picker, COPY REPORT, Auto-Kill's side task) —
    // never from the UI thread, never from the Auto-Kill tick body. The
    // first call of an attach may walk the object list (~0.3 s); after
    // that it is a dictionary read.
    public static void Ensure()
    {
        lock (_runGate)
        {
            var now = (Base.AttachedPid, Base.Instance.AttachmentGeneration);
            int attempt;
            lock (_gate)
            {
                if (now != _session) { _session = now; _decided = false; _retryAt = 0; _attempts = 0; _verdicts.Clear(); _liveSizes.Clear(); }
                if (_decided || System.Environment.TickCount64 < _retryAt) return;
                if (Base.AttachedPid == 0) { _summary = _report = "scanner not attached"; return; }
                attempt = ++_attempts;
            }

            // A retry re-walks first: class lookups read the last walk's
            // snapshot, and a class missing from it (attached while the game
            // was still loading its packages) stays missing until something
            // walks again.
            if (attempt > 1) GameReflection.RefreshClasses();

            bool transient = false;
            var classes = new Dictionary<string, (int cls, string why)>();
            // Trust is decided from the classes an object walk records; on a
            // cold attach nothing has walked yet, so look a class up first
            // (that walks once) instead of reporting "waiting" for nothing.
            bool? trusted = GameReflection.Trusted;
            if (trusted == null) { ClassOf("Core", "Object", classes); trusted = GameReflection.Trusted; }
            var verdicts = new Dictionary<(string, string), Verdict>();
            foreach (var r in Rows) verdicts[(r.Class, r.Field)] = Resolve(r, trusted, classes, attempt, ref transient);
            var sizes = new Dictionary<string, int>();
            if (trusted == true)
                foreach (var (package, cls, _) in ClassSizes)
                {
                    int c = ClassOf(package, cls, classes).cls;
                    if (c != 0) sizes[cls] = GameReflection.ClassPropertySize(c);
                }

            lock (_gate)
            {
                if (now != _session) return; // re-attached mid-check: the next call starts over
                _verdicts.Clear();
                foreach (var kv in verdicts) _verdicts[kv.Key] = kv.Value;
                _liveSizes.Clear();
                foreach (var kv in sizes) _liveSizes[kv.Key] = kv.Value;
                _decided = !transient;
                _retryAt = transient ? System.Environment.TickCount64 + 5_000 : 0;
                Summarize();
            }
        }
    }

    // "class X is not loaded" is retried this many checks (5 s apart) before
    // it counts as final — long enough for a still-loading game to finish.
    private const int NotLoadedRetries = 6;

    private static (int cls, string why) ClassOf(string package, string leaf, Dictionary<string, (int, string)> cache)
    {
        string key = package + "." + leaf;
        if (!cache.TryGetValue(key, out var hit))
        {
            int c = GameReflection.ClassByName(package, leaf, out string why);
            cache[key] = hit = (c, why);
        }
        return hit;
    }

    private static Verdict Resolve(Row r, bool? trusted, Dictionary<string, (int, string)> classes, int attempt, ref bool transient)
    {
        if (trusted == null) { transient = true; return new(Kind.Unknown, -1, 0, "reflection waiting"); }
        if (trusted == false) return new(Kind.Unknown, -1, 0, "reflection off this attach");
        var (cls, why) = ClassOf(r.Package, r.Class, classes);
        if (cls == 0)
        {
            if (why == "object list unavailable" || (why.EndsWith(" is not loaded") && attempt < NotLoadedRetries))
                transient = true;
            return new(Kind.Unknown, -1, 0, why);
        }
        bool torn;
        if (r.Mask != 0)
        {
            var (off, mask) = GameReflection.ClassBoolField(cls, r.Field, out torn);
            if (off < 0)
            {
                if (torn) transient = true;
                return new(Kind.Unknown, -1, 0, torn ? "torn read" : "no bool property by that name (or bool masks unproven)");
            }
            return new(off == r.Expected && mask == r.Mask ? Kind.Match : Kind.Moved, off, mask, "");
        }
        int at = GameReflection.ClassFieldOffset(cls, r.Field, out torn);
        if (at < 0)
        {
            if (torn) transient = true;
            return new(Kind.Unknown, -1, 0, torn ? "torn read" : "no property by that name");
        }
        return new(at == r.Expected ? Kind.Match : Kind.Moved, at, 0, "");
    }

    private static string Where(int offset, uint mask) => mask != 0 ? $"+0x{offset:X}:0x{mask:X}" : $"+0x{offset:X}";

    private static void Summarize()
    {
        int match = 0;
        var moved = new List<string>();
        var unknown = new List<string>();
        foreach (var r in Rows)
        {
            var v = _verdicts[(r.Class, r.Field)];
            string name = $"{r.Class}.{r.Field}";
            if (v.Kind == Kind.Match) match++;
            else if (v.Kind == Kind.Moved)
                moved.Add($"MOVED   {name,-48} tool {Where(r.Expected, r.Mask),-14} game {Where(v.Actual, v.ActualMask),-14} [{r.Consumer}]");
            else unknown.Add($"unknown {name,-48} {v.Reason}");
        }
        // Unknowns are usually one cause for many rows (waiting / off): name the causes, not every row.
        var causes = Rows.Select(r => _verdicts[(r.Class, r.Field)]).Where(v => v.Kind == Kind.Unknown)
                         .GroupBy(v => v.Reason).Select(g => $"{g.Count()} {g.Key}");
        _summary = $"Layout: {match}/{Rows.Length} match" +
                   (moved.Count > 0 ? $", {moved.Count} MOVED" : "") +
                   (unknown.Count > 0 ? $", unknown: {string.Join(", ", causes)}" : "");

        var sizes = ClassSizes.Select(s =>
            !_liveSizes.TryGetValue(s.cls, out int live) || live < 0 ? $"{s.cls} ?"
            : live == s.size ? $"{s.cls} 0x{live:X}" : $"{s.cls} 0x{live:X} (was 0x{s.size:X} — GREW/SHRANK)");
        var lines = new List<string> { _summary };
        lines.AddRange(moved);
        // Every row unknown for one reason (splash screen, reader off) needs no per-row list.
        if (unknown.Count > 0 && match + moved.Count > 0) lines.AddRange(unknown);
        if (_liveSizes.Count > 0) lines.Add("sizes: " + string.Join(" · ", sizes));
        _report = string.Join("\n", lines);

        if (_summary != _logged)
        {
            _logged = _summary;
            Base.LogEvent(_summary);
            foreach (string m in moved) Base.LogEvent("Layout: " + m);
        }
    }

    // ── Lookups (dictionary reads — safe from the UI thread) ──────────

    // The verdict for one row; Unknown("not checked yet") before Ensure
    // has run this attach.
    public static Verdict Of(string cls, string field)
    {
        lock (_gate)
            return Same() && _verdicts.TryGetValue((cls, field), out var v) ? v : new(Kind.Unknown, -1, 0, "not checked yet");
    }

    // The game's offset for a row: true only on Match or Moved.
    public static bool TryActual(string cls, string field, out int offset, out uint mask)
    {
        var v = Of(cls, field);
        offset = v.Actual; mask = v.ActualMask;
        return v.Kind != Kind.Unknown;
    }

    // "" unless the row is Moved; else "Class.Field moved to +0x.. on this
    // game build (tool uses +0x..)" for a pause message.
    public static string MovedNote(string cls, string field)
    {
        var v = Of(cls, field);
        if (!v.IsMoved) return "";
        var r = Rows.First(x => x.Class == cls && x.Field == field);
        return $"{cls}.{field} moved to {Where(v.Actual, v.ActualMask)} on this game build (tool uses {Where(r.Expected, r.Mask)})";
    }

    // Live instance size of a class this attach (-1 = not checked).
    public static int ClassSize(string cls)
    {
        lock (_gate) return Same() && _liveSizes.TryGetValue(cls, out int s) ? s : -1;
    }

    // ── Item / hero block write gate ───────────────────────────────────
    //
    // ItemNative / HeroNative are fixed [StructLayout] mirrors written whole.
    // If a patch moves a field INSIDE the block, a whole-struct write lands
    // every later value in the wrong place. Refuse only on a positive Moved
    // verdict, naming the field; Unknown keeps today's behavior (reflection
    // is legitimately off at the splash screen — refusing every edit there
    // would turn a diagnostic into an outage). Reads are never gated.
    public static bool ItemBlockOk(out string why) => BlockOk(Block.Item, ItemBase, "Item editing", out why);

    // Hero offsets are compared relative to HeroHealthModifier (the block's
    // first field): a field inserted BEFORE the block shifts it whole, and
    // the hero readers follow it by name (HeroFields) — only a shift inside
    // the block breaks a whole-struct write.
    public static bool HeroBlockOk(out string why)
    {
        int heroBase = TryActual("DunDefHero", "HeroHealthModifier", out int b, out _) ? b : HeroBase;
        return BlockOk(Block.Hero, heroBase, "Hero editing", out why);
    }

    private static bool BlockOk(Block block, int blockBase, string feature, out string why)
    {
        why = "";
        foreach (var r in Rows)
        {
            if (r.Block != block) continue;
            // Every RESOLVED row, Match included: when the hero block's base
            // moves, a field that kept its old absolute offset (Match) has
            // shifted relative to the base just the same.
            var v = Of(r.Class, r.Field);
            if (v.Kind == Kind.Unknown) continue;
            if (v.Actual - blockBase == r.BlockOffset && v.ActualMask == r.Mask) continue;
            string name = r.ToolName == r.Field ? r.Field : $"{r.Field} (the tool's {r.ToolName})";
            why = $"the game moved {name} on this build — the tool expects +0x{r.BlockOffset:X} " +
                  $"in the {(block == Block.Item ? "item" : "hero")} data, the game has it at +0x{v.Actual - blockBase:X}. " +
                  $"{feature} is turned off until the tool is updated (viewing still works).";
            return false;
        }
        return true;
    }
}
