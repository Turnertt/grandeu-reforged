using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Modinator;

// A portable value-set, NEVER a serialized ItemNative. No item IDs, process
// addresses, padding, FString/TArray headers or engine flags enter this file.
internal sealed class ItemTemplate
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string ItemName { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    // Version 1 files from the initial draft may contain DefaultMode. It is
    // intentionally ignored: every source now uses the original Dupe policy.
    public DateTime? ModifiedUtc { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(EquipmentName)
        ? ItemName : Watermark.StripColorTags(EquipmentName);
    public ItemDupeValues Values { get; set; } = new();
    public string EquipmentName { get; set; } = "";
    public string Description { get; set; } = "";
    public string ForgerName { get; set; } = "";
    // Equipment archetype, four resistance UClasses, elemental UClass.
    // Null native references use the empty string. Others use class + full
    // UObject path, resolved afresh in the currently attached game.
    public string[] References { get; set; } = new string[6];

    public void Validate()
    {
        if (SchemaVersion != 1 || Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 120)
            throw new InvalidDataException("Unsupported or invalid item template.");
        if (ItemName == null || EquipmentName == null || Description == null || ForgerName == null ||
            ItemName.Length > 16384 || EquipmentName.Length > 8192 || Description.Length > 8192 || ForgerName.Length > 8192)
            throw new InvalidDataException("Template text is missing or too long.");
        if (new[] { Name, ItemName, EquipmentName, Description, ForgerName }.Any(s => s.Contains('\0')))
            throw new InvalidDataException("Template text contains a null character.");
        if (References == null || References.Length != 6 || string.IsNullOrEmpty(References[0]) ||
            References.Any(p => p == null || p.Length > 4096))
            throw new InvalidDataException("Template item references are incomplete. Save it again from a live item.");
        if (Values == null) throw new InvalidDataException("Template values are missing.");
        Values.Validate();
        int level = Values.Scalars[nameof(ItemNative.Level)].GetInt32();
        int max = Values.Scalars[nameof(ItemNative.MaxEquipmentLevel)].GetInt32();
        // Level above max is allowed: edited items have it, and the live
        // dupe path accepts them (DupeMemory.ReadItem only bounds each value).
        if (level < 0 || level > 1000000 || max < 0 || max > 1000000)
            throw new InvalidDataException("Template upgrade levels are invalid.");
    }

    public ItemTemplate Copy(bool newIdentity = false)
    {
        Validate();
        var copy = JsonSerializer.Deserialize<ItemTemplate>(JsonSerializer.Serialize(this))!;
        if (newIdentity)
        {
            copy.Id = Guid.NewGuid();
            copy.CreatedUtc = DateTime.UtcNow;
            copy.ModifiedUtc = null;
        }
        return copy;
    }
}

internal sealed class ItemDupeValues
{
    // Explicit transfer allowlist. Reflection is only used against these
    // compile-time field names; data from a template never selects a field.
    private static readonly string[] StatFields =
    [
        nameof(ItemNative.WeaponDamageBonus), nameof(ItemNative.WeaponNumberOfProjectilesBonus),
        nameof(ItemNative.WeaponSpeedOfProjectilesBonus), nameof(ItemNative.MaxRandomElementalDamageMultiplier),
        nameof(ItemNative.WeaponSwingSpeedMultiplier), nameof(ItemNative.WeaponReloadSpeedBonus),
        nameof(ItemNative.WeaponKnockbackBonus), nameof(ItemNative.WeaponAltDamageBonus),
        nameof(ItemNative.WeaponBlockingBonus), nameof(ItemNative.WeaponClipAmmoBonus),
        nameof(ItemNative.AdditionalAllowedUpgradeResistancePoints), nameof(ItemNative.RequirementLevelOverride),
        nameof(ItemNative.WeaponChargeSpeedBonus), nameof(ItemNative.WeaponShotsPerSecondBonus),
        nameof(ItemNative.ManualLR), nameof(ItemNative.MaximumSellWorth), nameof(ItemNative.MinimumSellWorth),
        nameof(ItemNative.ShopMinimumSellWorth), nameof(ItemNative.MaxEquipmentLevel), nameof(ItemNative.Level),
        nameof(ItemNative.StoredMana), nameof(ItemNative.MyRatingPercent), nameof(ItemNative.MyRating)
    ];
    private static readonly string[] AppearanceFields =
    [
        nameof(ItemNative.WeaponDrawScaleMultiplier), nameof(ItemNative.NameIndex_Base),
        nameof(ItemNative.NameIndex_QualityDescriptor), nameof(ItemNative.NameIndex_DamageReduction),
        nameof(ItemNative.PrimaryColorSet), nameof(ItemNative.SecondaryColorSet), nameof(ItemNative.EquipmentType)
    ];
    private static readonly FieldInfo[] Fields = StatFields.Concat(AppearanceFields)
        .Select(n => typeof(ItemNative).GetField(n)!).ToArray();

    public Dictionary<string, JsonElement> Scalars { get; set; } = new();
    public int[] Stats { get; set; } = new int[10];
    public int[] Resistances { get; set; } = new int[4];
    public int ElementalDamage { get; set; }
    public float[] PrimaryColor { get; set; } = new float[4];
    public float[] SecondaryColor { get; set; } = new float[4];
    // The source item's default colors (its selected color set entries), in
    // the same [A, R, G, B] order. Not applied by a copy — the target keeps
    // its own tables. They let the template editor show the real color
    // instead of black while no override is set, and keep the other color
    // when only one is edited. Optional: absent in older templates.
    public float[]? DefaultPrimaryColor { get; set; }
    public float[]? DefaultSecondaryColor { get; set; }
    // Item DEFINITION state from the game's FEquipmentNetInfo (2026-09-27):
    // the +0xA8 behavior word (instance-state bits are masked out on apply)
    // and the nine definition bytes bCantBeDropped, bCantBeSold,
    // bAutoLockInItemBox (R1), bDidOnetimeEffect (Mystery),
    // bHideQualityDescriptors, bEquipmentFeatureByte1/2 + 2 pad (R2).
    // Optional so templates saved before this keep working (target's kept).
    public int? DefinitionFlags { get; set; }
    public byte[]? DefinitionBytes { get; set; }
    // The numeric part of the block ItemNative calls _InstancePad, all of it
    // in the game's FEquipmentNetInfo / save struct (DD1_INTERNALS.md §8c):
    // StatEquipmentIDs[10], StatEquipmentTiers[10], QualityBeamColorOverride
    // (R, G, B, A) and FeatureArray[10]. The two strings and the unique ID in
    // that block (timestamp with the owner's Steam ID, feature string) are
    // NOT captured and always stay the target's. Optional: absent in
    // templates saved before 2026-09-30 (the target's values are kept).
    public int[]? StatEquipmentIDs { get; set; }
    public int[]? StatEquipmentTiers { get; set; }
    public float[]? QualityBeamColor { get; set; }
    public int[]? FeatureArray { get; set; }

    // Byte ranges inside _InstancePad (object +0x140).
    private const int PadStatIds = 0, PadStatTiers = 40, PadBeamColor = 80, PadFeatures = 96, PadNumericEnd = 136;

    // Fallback instance-state mask (live-verified bit names, current build);
    // callers with a live object pass DupeMemory.InstanceStateMask instead.
    public const uint DefaultInstanceMask = 0x8000 | 0x10000 | 0x20000 | 0x80000 | 0x100000 | 0x200000;

    public static ItemDupeValues Capture(in ItemNative n)
    {
        var v = new ItemDupeValues
        {
            Stats = (int[])n.StatModifiers.Clone(),
            Resistances = n.DamageReductions.Select(d => d.Value).ToArray(),
            ElementalDamage = n.WeaponAdditionalDamage.Value,
            PrimaryColor = ColorValues(n.PrimaryColorOverride),
            SecondaryColor = ColorValues(n.SecondaryColorOverride),
            DefinitionFlags = n.Flags,
            DefinitionBytes = DefinitionBytesOf(n)
        };
        if (n._InstancePad is { Length: >= PadNumericEnd } pad)
        {
            v.StatEquipmentIDs = Ints(pad, PadStatIds);
            v.StatEquipmentTiers = Ints(pad, PadStatTiers);
            v.QualityBeamColor = Enumerable.Range(0, 4).Select(i => BitConverter.ToSingle(pad, PadBeamColor + i * 4)).ToArray();
            v.FeatureArray = Ints(pad, PadFeatures);
            if (v.QualityBeamColor.Any(f => !float.IsFinite(f))) v.QualityBeamColor = null;
        }
        if (ItemColors.TryDefault(n, primary: true, out var primaryDefault)) v.DefaultPrimaryColor = ColorValues(primaryDefault);
        if (ItemColors.TryDefault(n, primary: false, out var secondaryDefault)) v.DefaultSecondaryColor = ColorValues(secondaryDefault);
        foreach (var f in Fields) v.Scalars.Add(f.Name, JsonSerializer.SerializeToElement(f.GetValue(n), f.FieldType));
        v.Validate();
        return v;
    }

    public void Validate()
    {
        if (Stats == null || Stats.Length != 10 || Resistances == null || Resistances.Length != 4 ||
            PrimaryColor == null || PrimaryColor.Length != 4 || SecondaryColor == null || SecondaryColor.Length != 4 ||
            Scalars == null || Scalars.Count != Fields.Length)
            throw new InvalidDataException("Template stat fields are incomplete.");
        if (PrimaryColor.Concat(SecondaryColor).Any(f => !float.IsFinite(f)))
            throw new InvalidDataException("Template contains a non-finite colour.");
        foreach (var ints in new[] { StatEquipmentIDs, StatEquipmentTiers, FeatureArray })
            if (ints != null && ints.Length != 10) throw new InvalidDataException("Template stat-equipment data is invalid.");
        if (QualityBeamColor != null && (QualityBeamColor.Length != 4 || QualityBeamColor.Any(f => !float.IsFinite(f))))
            throw new InvalidDataException("Template beam color is invalid.");
        foreach (var defaults in new[] { DefaultPrimaryColor, DefaultSecondaryColor })
            if (defaults != null && (defaults.Length != 4 || defaults.Any(f => !float.IsFinite(f))))
                throw new InvalidDataException("Template default colors are invalid.");
        foreach (var f in Fields)
        {
            if (!Scalars.TryGetValue(f.Name, out var json)) throw new InvalidDataException("Missing field: " + f.Name);
            var value = json.Deserialize(f.FieldType) ?? throw new InvalidDataException("Missing value: " + f.Name);
            if (value is float number && !float.IsFinite(number)) throw new InvalidDataException("Non-finite value: " + f.Name);
        }
    }

    // Target defaults are load-bearing. Deep-copy edited arrays so preview
    // and validation cannot accidentally mutate the captured target snapshot.
    private static int[] Ints(byte[] pad, int offset)
        => Enumerable.Range(0, 10).Select(i => BitConverter.ToInt32(pad, offset + i * 4)).ToArray();

    private static byte[] DefinitionBytesOf(in ItemNative n)
    {
        var b = new byte[9];
        for (int i = 0; i < 3; i++) b[i] = n.R1 != null && n.R1.Length > i ? n.R1[i] : (byte)0;
        b[3] = n.Mystery;
        for (int i = 0; i < 5; i++) b[4 + i] = n.R2 != null && n.R2.Length > i ? n.R2[i] : (byte)0;
        return b;
    }

    public ItemNative Apply(ItemNative target) => Apply(target, DefaultInstanceMask);

    public ItemNative Apply(ItemNative target, uint instanceMask)
    {
        Validate();
        object boxed = target;
        foreach (var f in Fields)
            f.SetValue(boxed, Scalars[f.Name].Deserialize(f.FieldType));
        var merged = (ItemNative)boxed;
        merged.StatModifiers = (int[])Stats.Clone();
        merged.DamageReductions = target.DamageReductions == null ? new DamageNative[4] : (DamageNative[])target.DamageReductions.Clone();
        for (int i = 0; i < 4; i++) merged.DamageReductions[i].Value = Resistances[i];
        merged.WeaponAdditionalDamage.Value = ElementalDamage;
        merged.PrimaryColorOverride = Color(PrimaryColor);
        merged.SecondaryColorOverride = Color(SecondaryColor);
        // Definition state (source) over instance state (target) — see the
        // property comments. Absent in old templates → the target's stay.
        if (DefinitionFlags is int flags)
            merged.Flags = DupeMemory.MergeFlags(flags, target.Flags, instanceMask);
        if (DefinitionBytes is { Length: 9 } db)
        {
            merged.R1 = [db[0], db[1], db[2]];
            merged.Mystery = db[3];
            merged.R2 = [db[4], db[5], db[6], db[7], db[8]];
        }
        // Numeric _InstancePad values over a COPY of the target's block; the
        // strings and unique ID behind them are left exactly as they were.
        if (target._InstancePad is { Length: >= PadNumericEnd } targetPad)
        {
            byte[] pad = (byte[])targetPad.Clone();
            void Put(int[]? values, int offset)
            {
                if (values is { Length: 10 })
                    for (int i = 0; i < 10; i++) BitConverter.GetBytes(values[i]).CopyTo(pad, offset + i * 4);
            }
            Put(StatEquipmentIDs, PadStatIds);
            Put(StatEquipmentTiers, PadStatTiers);
            Put(FeatureArray, PadFeatures);
            if (QualityBeamColor is { Length: 4 })
                for (int i = 0; i < 4; i++) BitConverter.GetBytes(QualityBeamColor[i]).CopyTo(pad, PadBeamColor + i * 4);
            merged._InstancePad = pad;
        }
        return merged;
    }

    public ItemNative Preview() => Apply(new ItemNative());
    // File format is [A, R, G, B] (unchanged for compatibility with saved
    // templates). Mapping is by name, so the 2026-09-27 realignment of
    // ItemNative (color overrides are R,G,B,A in memory) changes nothing here
    // — EXCEPT that templates captured before it hold, in slot 0, whatever
    // dword sat 4 bytes before the real color: an int (the previous TArray's
    // Max) reinterpreted as a float, i.e. a denormal near zero, or 1.0f where
    // the old tool had written its "alpha" there. Applying that as the real
    // alpha would render the item black, so alpha is sanitized on apply: a
    // value outside (0.001, 1] becomes 1 (full). New captures store the true
    // alpha and pass through unchanged.
    private static float[] ColorValues(LinearColorNative c) => [c.A, c.R, c.G, c.B];
    private static LinearColorNative Color(float[] c) => new() { A = SaneAlpha(c[0]), R = c[1], G = c[2], B = c[3] };
    private static float SaneAlpha(float a) => a > 0.001f && a <= 1f ? a : 1f;
}

internal static class ItemTemplateLibrary
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Modinator", "item-templates");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<ItemTemplate> Load(out int unreadable)
    {
        unreadable = 0;
        var result = new List<ItemTemplate>();
        if (!Directory.Exists(Folder)) return result;
        foreach (string path in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException();
                var entry = JsonSerializer.Deserialize<ItemTemplate>(File.ReadAllText(path), Json) ?? throw new InvalidDataException();
                entry.Validate();
                if (Path.GetFileNameWithoutExtension(path) != entry.Id.ToString("N")) throw new InvalidDataException();
                result.Add(entry);
            }
            catch { unreadable++; }
        }
        return result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void Save(ItemTemplate entry)
    {
        entry.Validate();
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, entry.Id.ToString("N") + ".json");
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entry, Json));
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(ItemTemplate entry) => File.Delete(Path.Combine(Folder, entry.Id.ToString("N") + ".json"));
}
