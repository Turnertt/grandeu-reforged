using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Modinator;

// Class-aware MAX compatibility — the single source of truth shared by
// ItemEditView (single-item, textbox path) and MaxItemConfig.ApplyTo
// (bulk, ItemNative path). Derived from live weapon_probe dumps; see
// max_item_weapontype_research.md.
internal enum WeaponStat
{
    Damage, RangedDamage, Blocking, Knockback, ChargeSpeed, ShotsPerSecond,
    NumProjectiles, ProjectileSpeed, ClipAmmo, ReloadSpeed, DrawScale, SwingSpeed
}

internal static class MaxCompat
{
    // weaponType (EWeaponType byte) is at object+0x824 = item-Address +
    // 0x7EC — outside the marshaled ItemNative. Read 1 byte for weapons.
    public const int WeaponTypeOffset = 0x7EC;

    // Set on every weapon family in testing.
    private static readonly HashSet<WeaponStat> Universal = new()
        { WeaponStat.Damage, WeaponStat.DrawScale, WeaponStat.SwingSpeed };

    // Per-EWeaponType additions: 1 Apprentice / 2 Squire / 3 Initiate(gun) /
    // 4 Recruit. 0 Anyone / 5 None / unknown add nothing — the present-only
    // ("special/unique item") union still maxes whatever the item carries.
    private static HashSet<WeaponStat> Family(byte weaponType) => weaponType switch
    {
        1 => new() { WeaponStat.NumProjectiles, WeaponStat.ProjectileSpeed, WeaponStat.Knockback, WeaponStat.ChargeSpeed },
        2 => new() { WeaponStat.Knockback, WeaponStat.Blocking },
        3 => new() { WeaponStat.ProjectileSpeed, WeaponStat.ReloadSpeed, WeaponStat.ClipAmmo, WeaponStat.ShotsPerSecond },
        4 => new() { WeaponStat.NumProjectiles, WeaponStat.ProjectileSpeed },
        _ => new(),
    };

    // Universal-for-weapons OR family-applicable for this weaponType OR the
    // item already carries it (special/unique items — max whatever they
    // actually have, no matter the class). Pets never get SwingSpeed.
    public static bool WeaponStatApplies(WeaponStat s, EquipmentType type, byte weaponType, bool present)
    {
        if (type == EquipmentType.Familiar && s == WeaponStat.SwingSpeed) return false;
        bool isWeapon = type == EquipmentType.Weapon;
        return present || (isWeapon && (Universal.Contains(s) || Family(weaponType).Contains(s)));
    }

    // Resistances: armor/accessory unconditionally; weapons/pets only if the
    // item already carries the resist (special items).
    public static bool ResistApplies(EquipmentType type, bool present)
        => present || (type != EquipmentType.Weapon && type != EquipmentType.Familiar);

    // Elemental damage: every weapon (even at 0), or anywhere already set.
    public static bool ElementalApplies(EquipmentType type, bool present)
        => present || type == EquipmentType.Weapon;
}

// Per-field "max" values for the MAX button in ItemEditView. Nullable types
// mean "skip this field entirely when MAX runs" (not just "zero"). The apply
// rules live in ItemEditView — this class is just data + persistence.
public class MaxItemConfig
{
    // Hero stats — always applied when MAX runs.
    public int? HeroHealth { get; set; }
    public int? HeroSpeed { get; set; }
    public int? HeroDamage { get; set; }
    public int? HeroCasting { get; set; }
    public int? HeroSkill1 { get; set; }
    public int? HeroSkill2 { get; set; }

    // Tower stats — always applied when MAX runs.
    public int? TowerHealth { get; set; }
    public int? TowerSpeed { get; set; }
    public int? TowerDamage { get; set; }
    public int? TowerRange { get; set; }

    // Weapon bonuses — only applied when the item's current value is non-zero.
    public int? Damage { get; set; }
    public int? RangedDamage { get; set; }
    public int? Blocking { get; set; }
    public int? Knockback { get; set; }
    public int? ChargeSpeed { get; set; }
    public int? ShotsPerSecond { get; set; }
    public int? NumProjectiles { get; set; }
    public int? ProjectileSpeed { get; set; }
    public int? ClipAmmo { get; set; }
    public int? ReloadSpeed { get; set; }
    public float? DrawScale { get; set; }
    public float? SwingSpeed { get; set; }

    // Resistances — only-if-nonzero.
    public int? Generic { get; set; }
    public int? Poison { get; set; }
    public int? Fire { get; set; }
    public int? Lightning { get; set; }

    public int? ElementalDamage { get; set; }

    // Quality — only-if-nonzero.
    public int? Quality1 { get; set; }
    public int? QualityFlag { get; set; }

    // Level / identity — only-if-nonzero.
    public int? Level { get; set; }
    public int? MaxLevel { get; set; }
    public int? StoredMana { get; set; }
    public int? LevelRequirement { get; set; }
    public int? ID1 { get; set; }
    public int? ID2 { get; set; }

    // Economy — only-if-nonzero.
    public int? MaxValue { get; set; }
    public int? MinValue { get; set; }
    public float? Rating { get; set; }
    public float? RatingPercent { get; set; }

    // Strings — always applied if non-empty.
    public string? Description { get; set; }
    public string? ForgerName { get; set; }

    private static string Path =>
        System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Modinator", "max_item.json");

    public static MaxItemConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                string json = File.ReadAllText(Path);
                var cfg = JsonSerializer.Deserialize<MaxItemConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new MaxItemConfig();
    }

    public void Save()
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // Apply the configured max values to `item` using its own current state to
    // decide which rule applies per field. Strings (Description/ForgerName)
    // are NOT touched here because the native's string fields are NativeArray
    // pointers — the caller should use WriteUniInPlace/WriteUni for those.
    //
    // Returns the modified ItemNative. Mutates the input struct via reassignment
    // since ItemNative is a value type.
    internal ItemNative ApplyTo(ItemNative item, byte weaponType)
    {
        ItemUser cur = Base.ItemToUser(item);
        EquipmentType t = cur.EquipmentType;

        // Always-apply: hero + tower stats.
        if (HeroHealth.HasValue)  item.StatModifiers[0] = HeroHealth.Value;
        if (HeroSpeed.HasValue)   item.StatModifiers[1] = HeroSpeed.Value;
        if (HeroDamage.HasValue)  item.StatModifiers[2] = HeroDamage.Value;
        if (HeroCasting.HasValue) item.StatModifiers[3] = HeroCasting.Value;
        if (HeroSkill1.HasValue)  item.StatModifiers[4] = HeroSkill1.Value;
        if (HeroSkill2.HasValue)  item.StatModifiers[5] = HeroSkill2.Value;
        if (TowerHealth.HasValue) item.StatModifiers[6] = TowerHealth.Value;
        if (TowerSpeed.HasValue)  item.StatModifiers[7] = TowerSpeed.Value;
        if (TowerDamage.HasValue) item.StatModifiers[8] = TowerDamage.Value;
        if (TowerRange.HasValue)  item.StatModifiers[9] = TowerRange.Value;

        // Weapon bonuses — class-aware (shared MaxCompat). A stat is maxed
        // when it's universal-for-weapons, family-applicable for this
        // weaponType, OR the item already carries it (special/unique items).
        if (Damage.HasValue          && MaxCompat.WeaponStatApplies(WeaponStat.Damage,          t, weaponType, item.WeaponDamageBonus              != 0))  item.WeaponDamageBonus              = Damage.Value;
        if (RangedDamage.HasValue    && MaxCompat.WeaponStatApplies(WeaponStat.RangedDamage,    t, weaponType, item.WeaponAltDamageBonus           != 0))  item.WeaponAltDamageBonus           = RangedDamage.Value;
        if (Blocking.HasValue        && MaxCompat.WeaponStatApplies(WeaponStat.Blocking,        t, weaponType, item.WeaponBlockingBonus            != 0))  item.WeaponBlockingBonus            = Blocking.Value;
        if (Knockback.HasValue       && MaxCompat.WeaponStatApplies(WeaponStat.Knockback,       t, weaponType, item.WeaponKnockbackBonus           != 0))  item.WeaponKnockbackBonus           = Knockback.Value;
        if (ChargeSpeed.HasValue     && MaxCompat.WeaponStatApplies(WeaponStat.ChargeSpeed,     t, weaponType, item.WeaponChargeSpeedBonus         != 0))  item.WeaponChargeSpeedBonus         = ChargeSpeed.Value;
        if (ShotsPerSecond.HasValue  && MaxCompat.WeaponStatApplies(WeaponStat.ShotsPerSecond,  t, weaponType, item.WeaponShotsPerSecondBonus      != 0))  item.WeaponShotsPerSecondBonus      = ShotsPerSecond.Value;
        if (NumProjectiles.HasValue  && MaxCompat.WeaponStatApplies(WeaponStat.NumProjectiles,  t, weaponType, item.WeaponNumberOfProjectilesBonus != 0))  item.WeaponNumberOfProjectilesBonus = NumProjectiles.Value;
        if (ProjectileSpeed.HasValue && MaxCompat.WeaponStatApplies(WeaponStat.ProjectileSpeed, t, weaponType, item.WeaponSpeedOfProjectilesBonus  != 0))  item.WeaponSpeedOfProjectilesBonus  = ProjectileSpeed.Value;
        if (ClipAmmo.HasValue        && MaxCompat.WeaponStatApplies(WeaponStat.ClipAmmo,        t, weaponType, item.WeaponClipAmmoBonus            != 0))  item.WeaponClipAmmoBonus            = ClipAmmo.Value;
        if (ReloadSpeed.HasValue     && MaxCompat.WeaponStatApplies(WeaponStat.ReloadSpeed,     t, weaponType, item.WeaponReloadSpeedBonus         != 0))  item.WeaponReloadSpeedBonus         = ReloadSpeed.Value;
        if (DrawScale.HasValue       && MaxCompat.WeaponStatApplies(WeaponStat.DrawScale,       t, weaponType, item.WeaponDrawScaleMultiplier      != 0f)) item.WeaponDrawScaleMultiplier      = DrawScale.Value;
        if (SwingSpeed.HasValue      && MaxCompat.WeaponStatApplies(WeaponStat.SwingSpeed,      t, weaponType, item.WeaponSwingSpeedMultiplier     != 0f)) item.WeaponSwingSpeedMultiplier     = SwingSpeed.Value;

        // Resistances: armor/accessory unconditionally; weapons/pets only if
        // the item already carries the resist (special items).
        if (Generic.HasValue   && MaxCompat.ResistApplies(t, item.DamageReductions[0].Value != 0)) item.DamageReductions[0].Value = Generic.Value;
        if (Poison.HasValue    && MaxCompat.ResistApplies(t, item.DamageReductions[1].Value != 0)) item.DamageReductions[1].Value = Poison.Value;
        if (Fire.HasValue      && MaxCompat.ResistApplies(t, item.DamageReductions[2].Value != 0)) item.DamageReductions[2].Value = Fire.Value;
        if (Lightning.HasValue && MaxCompat.ResistApplies(t, item.DamageReductions[3].Value != 0)) item.DamageReductions[3].Value = Lightning.Value;

        // Elemental damage: every weapon (even at 0), or anywhere already set.
        if (ElementalDamage.HasValue && MaxCompat.ElementalApplies(t, item.WeaponAdditionalDamage.Value != 0))
            item.WeaponAdditionalDamage.Value = ElementalDamage.Value;

        // Quality — only-if-nonzero.
        if (Quality1.HasValue    && item.NameIndex_Base != 0) item.NameIndex_Base = (byte)Quality1.Value;
        if (QualityFlag.HasValue && item.Mystery != 0)         item.Mystery        = (byte)QualityFlag.Value;

        // Level / identity — only-if-nonzero for most, but LevelRequirement
        // populates unconditionally since every item has one and users
        // expect MAX to set it regardless of current value.
        if (Level.HasValue            && item.Level             != 0) item.Level             = Level.Value;
        if (MaxLevel.HasValue         && item.MaxEquipmentLevel != 0) item.MaxEquipmentLevel = MaxLevel.Value;
        if (StoredMana.HasValue       && item.StoredMana        != 0) item.StoredMana        = StoredMana.Value;
        if (LevelRequirement.HasValue)                                item.ManualLR          = (byte)LevelRequirement.Value;
        if (ID1.HasValue              && item.EquipmentID1      != 0) item.EquipmentID1      = ID1.Value;
        if (ID2.HasValue              && item.EquipmentID2      != 0) item.EquipmentID2      = ID2.Value;

        // Economy — only-if-nonzero.
        if (MaxValue.HasValue      && item.MaximumSellWorth != 0)  item.MaximumSellWorth = MaxValue.Value;
        if (MinValue.HasValue      && item.MinimumSellWorth != 0)  item.MinimumSellWorth = MinValue.Value;
        if (Rating.HasValue        && item.MyRating         != 0f) item.MyRating         = Rating.Value;
        if (RatingPercent.HasValue && item.MyRatingPercent  != 0f) item.MyRatingPercent  = RatingPercent.Value;

        return item;
    }
}
