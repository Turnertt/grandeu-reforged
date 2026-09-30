namespace Modinator;

// Item display names from the game's own data (2026-09-27).
//
// What DD1 stores per UHeroEquipment (live-verified by name, DD1_INTERNALS.md
// §8b / §4d):
//   UserEquipmentName  +0x128  the custom / rolled name ("Spear of Light ",
//                              "Mana Token"); a single SPACE when unset.
//   EquipmentName      +0x21C  the ARCHETYPE's name — for generated loot a
//                              placeholder: "Boots Base", "Gauntlet Base",
//                              "Weapon Equipment Base", "Sword", "Staff".
//   RandomBaseNames    +0xA3C  TArray<FEG_StatMatchingString> (stride 0x18,
//                              StringValue FString at +0xC): the rolled base
//                              names ("Mail Boots", "Mega Chicken");
//   NameIndex_Base     +0xD0   which entry this item rolled.
// The game composes the shown name in UnrealScript (GetFullEquipmentName —
// not callable from outside). The Forge Viewer used to show the custom name,
// else the ARCHETYPE name, else the description, else a heuristic scan of
// nearby memory — so an unnamed generated item read "Gauntlet Base" and a
// blank one got whatever text happened to sit next to it. This resolver
// follows the game's data instead: custom name → rolled base name from the
// item's own table → archetype name (placeholder suffix " Base" stripped) →
// the archetype object's name. Quality is NOT folded in here; the cards show
// it in their own label. Read-only; all reads via Base.Instance.
internal static class ItemNames
{
    // Object-relative (UHeroEquipment*, not ItemNative) offsets. The string
    // ones are the ItemNative fields under their real names; the table is
    // outside ItemNative and read by name (reflection) with this
    // live-verified fallback.
    private const int OFF_USER_NAME        = 0x128;
    private const int OFF_ARCHETYPE_NAME   = 0x21C;
    private const int OFF_NAME_INDEX_BASE  = 0xD0;
    private const int OFF_RANDOM_BASE_NAMES = 0xA3C;
    private const int OFF_BEHAVIOR_FLAGS   = 0x3E0;
    private const uint MASK_ALLOW_NAME_RANDOMIZATION = 0x80000;
    private const int MatchingStringStride = 0x18;
    private const int MatchingStringValue  = 0x0C;
    private const string GenericPlaceholder = "Generic Random Item Name";

    // The name the item box shows (minus the quality word). `equipment` is
    // the UHeroEquipment object (ItemNative address − 0x38).
    public static string DisplayName(int equipment)
    {
        string custom = Clean(GameChain.ReadFString(equipment + OFF_USER_NAME, 8192));
        if (custom.Length > 0) return custom;
        return BaseName(equipment);
    }

    // The rolled base name ("Mail Boots"), else the archetype name with a
    // placeholder " Base" suffix dropped, else the archetype object's name.
    public static string BaseName(int equipment)
    {
        if (!GameChain.IsGamePtr(equipment)) return "";
        string rolled = RolledBaseName(equipment);
        if (rolled.Length > 0) return rolled;

        string arch = Clean(GameChain.ReadFString(equipment + OFF_ARCHETYPE_NAME));
        if (arch.Length == 0)
        {
            int template = GameChain.RdPtr(equipment);
            if (GameChain.IsGamePtr(template))
            {
                arch = Clean(GameChain.ReadFString(template + OFF_ARCHETYPE_NAME));
                if (arch.Length == 0) arch = PrettyObjectName(template);
            }
        }
        if (arch.EndsWith(" Base", System.StringComparison.Ordinal)) arch = arch[..^5].TrimEnd();
        return arch;
    }

    private static string RolledBaseName(int equipment)
    {
        try
        {
            // The table is copied onto every item from its class, but the
            // game only names an item from it when AllowNameRandomization is
            // set. A fixed-name item has the flag clear and keeps its
            // archetype name, even though its table is populated. Mask by
            // name; bit 19 of the +0x3E0 word is the fallback (SDK order).
            var allow = GameReflection.BoolField(equipment, "AllowNameRandomization");
            if (allow.offset <= 0 || allow.mask == 0) allow = (OFF_BEHAVIOR_FLAGS, MASK_ALLOW_NAME_RANDOMIZATION);
            if (((uint)GameChain.RdInt(equipment + allow.offset) & allow.mask) == 0) return "";

            byte[] idxB = Base.Instance.ReadMemory(equipment + OFF_NAME_INDEX_BASE, 1);
            int idx = idxB[0];
            int off = GameReflection.FieldOffset(equipment, "RandomBaseNames");
            if (off <= 0) off = OFF_RANDOM_BASE_NAMES;
            int data = GameChain.RdPtr(equipment + off);
            int num  = GameChain.RdInt(equipment + off + 4);
            int max  = GameChain.RdInt(equipment + off + 8);
            // Plausible TArray of a few name entries; the fallback offset is
            // only trusted when the header looks right.
            if (!GameChain.IsGamePtr(data) || num <= 0 || num > 256 || max < num || max > 256) return "";
            if (idx >= num) idx = 0;
            string s = Clean(GameChain.ReadFString(data + idx * MatchingStringStride + MatchingStringValue));
            return s == GenericPlaceholder ? "" : s;
        }
        catch { return ""; }
    }

    // "DunDefEquipment.ManaToken.ManaToken_Equipment" → "ManaToken Equipment".
    private static string PrettyObjectName(int obj)
    {
        string path = GameReflection.ObjectPath(obj);
        int dot = path.LastIndexOf('.');
        string leaf = dot >= 0 ? path[(dot + 1)..] : path;
        return leaf == "?" ? "" : leaf.Replace('_', ' ').Trim();
    }

    private static string Clean(string s) => (s ?? "").Trim();
}
