using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Modinator.Views;

public partial class BulkEditDialog : Window
{
    private readonly List<int> _addresses;
    private readonly int _structSize = Marshal.SizeOf(typeof(ItemNative));

    // Baseline values read from the first item (used for diff comparison).
    private int _baseHeroStats;
    private int _baseTowerStats;
    private int _baseResistances;
    private int _baseDamage;
    private int _baseRangedDamage;
    private int _baseElementalDamage;
    private int _baseBlocking;
    private int _baseKnockback;
    private int _baseChargeSpeed;
    private int _baseShotsPerSecond;
    private int _baseProjectiles;
    private int _baseProjectileSpeed;
    private int _baseClipAmmo;
    private int _baseReloadSpeed;
    private float _baseDrawScale;
    private float _baseSwingSpeed;
    private string _baseDescription = "";
    private string _baseForgerName = "";
    private int _baseLevel;
    private int _baseMaxLevel;
    private int _baseStoredMana;
    private byte _baseLevelRequirement;
    private int _baseColor1R, _baseColor1G, _baseColor1B;
    private int _baseColor2R, _baseColor2G, _baseColor2B;

    public int AppliedCount { get; private set; }
    public int FailedCount { get; private set; }

    public BulkEditDialog(List<int> addresses, string typeLabel)
    {
        InitializeComponent();

        _addresses = addresses;

        Title = $"Bulk Edit \u2014 {addresses.Count} items";
        TxtHeader.Text = $"Bulk Edit \u2014 {addresses.Count} {typeLabel}";

        LoadBaseline();
    }

    private void LoadBaseline()
    {
        int address = _addresses[0];

        byte[] raw = Base.Instance.ReadMemory(address, _structSize);
        ItemNative baseline = Base.Push<ItemNative>(raw);

        // Hero stats: use first hero stat as the uniform value.
        _baseHeroStats = baseline.StatModifiers[0];
        _baseTowerStats = baseline.StatModifiers[6];
        _baseResistances = baseline.DamageReductions[0].Value;
        _baseDamage = baseline.WeaponDamageBonus;
        _baseRangedDamage = baseline.WeaponAltDamageBonus;
        _baseElementalDamage = baseline.WeaponAdditionalDamage.Value;
        _baseBlocking = baseline.WeaponBlockingBonus;
        _baseKnockback = baseline.WeaponKnockbackBonus;
        _baseChargeSpeed = baseline.WeaponChargeSpeedBonus;
        _baseShotsPerSecond = baseline.WeaponShotsPerSecondBonus;
        _baseProjectiles = baseline.WeaponNumberOfProjectilesBonus;
        _baseProjectileSpeed = baseline.WeaponSpeedOfProjectilesBonus;
        _baseClipAmmo = baseline.WeaponClipAmmoBonus;
        _baseReloadSpeed = baseline.WeaponReloadSpeedBonus;
        _baseDrawScale = baseline.WeaponDrawScaleMultiplier;
        _baseSwingSpeed = baseline.WeaponSwingSpeedMultiplier;
        _baseLevel = baseline.Level;
        _baseMaxLevel = baseline.MaxEquipmentLevel;
        _baseStoredMana = baseline.StoredMana;
        _baseLevelRequirement = baseline.ManualLR;

        _baseDescription = Base.ReadUni<ItemNative>(address, "Description") ?? "";
        _baseForgerName = Base.ReadUni<ItemNative>(address, "ForgerName") ?? "";

        // Color overrides — round-trip through LinearColor so we get ints that
        // round-trip cleanly on compare. HDR values (|value| > 255) survive on
        // the game side because we keep the float LinearColor for writes.
        var bUser = Base.ItemToUser(baseline);
        _baseColor1R = bUser.Color1Override?.R ?? 0;
        _baseColor1G = bUser.Color1Override?.G ?? 0;
        _baseColor1B = bUser.Color1Override?.B ?? 0;
        _baseColor2R = bUser.Color2Override?.R ?? 0;
        _baseColor2G = bUser.Color2Override?.G ?? 0;
        _baseColor2B = bUser.Color2Override?.B ?? 0;

        // Show baseline values as grey placeholder text. Empty-on-Apply means
        // "no change" — matches the Item/Hero edit UX. Color R/G/B boxes
        // stay populated because the live preview swatch reads from them.
        SetHint(TxtHeroStats, _baseHeroStats.ToString());
        SetHint(TxtTowerStats, _baseTowerStats.ToString());
        SetHint(TxtResistances, _baseResistances.ToString());
        SetHint(TxtDamage, _baseDamage.ToString());
        SetHint(TxtRangedDamage, _baseRangedDamage.ToString());
        SetHint(TxtElementalDamage, _baseElementalDamage.ToString());
        SetHint(TxtBlocking, _baseBlocking.ToString());
        SetHint(TxtKnockback, _baseKnockback.ToString());
        SetHint(TxtChargeSpeed, _baseChargeSpeed.ToString());
        SetHint(TxtShotsPerSecond, _baseShotsPerSecond.ToString());
        SetHint(TxtProjectiles, _baseProjectiles.ToString());
        SetHint(TxtProjectileSpeed, _baseProjectileSpeed.ToString());
        SetHint(TxtClipAmmo, _baseClipAmmo.ToString());
        SetHint(TxtReloadSpeed, _baseReloadSpeed.ToString());
        SetHint(TxtDrawScale, _baseDrawScale.ToString(CultureInfo.InvariantCulture));
        SetHint(TxtSwingSpeed, _baseSwingSpeed.ToString(CultureInfo.InvariantCulture));
        SetHint(TxtDescription, _baseDescription ?? string.Empty);
        SetHint(TxtForgerName, _baseForgerName ?? string.Empty);
        SetHint(TxtLevel, _baseLevel.ToString());
        SetHint(TxtMaxLevel, _baseMaxLevel.ToString());
        SetHint(TxtStoredMana, _baseStoredMana.ToString());
        SetHint(TxtLevelRequirement, _baseLevelRequirement.ToString());
        TxtColor1R.Text = _baseColor1R.ToString();
        TxtColor1G.Text = _baseColor1G.ToString();
        TxtColor1B.Text = _baseColor1B.ToString();
        TxtColor2R.Text = _baseColor2R.ToString();
        TxtColor2G.Text = _baseColor2G.ToString();
        TxtColor2B.Text = _baseColor2B.ToString();
        UpdateColorPreview(TxtColor1R, TxtColor1G, TxtColor1B, Color1Preview);
        UpdateColorPreview(TxtColor2R, TxtColor2G, TxtColor2B, Color2Preview);
    }

    // ── Color previews + picker ─────────────────────────────────────

    private void Color1_Changed(object sender, TextChangedEventArgs e)
        => UpdateColorPreview(TxtColor1R, TxtColor1G, TxtColor1B, Color1Preview);

    private void Color2_Changed(object sender, TextChangedEventArgs e)
        => UpdateColorPreview(TxtColor2R, TxtColor2G, TxtColor2B, Color2Preview);

    private static void UpdateColorPreview(TextBox r, TextBox g, TextBox b, Border preview)
    {
        if (preview == null || r == null || g == null || b == null) return;
        int.TryParse(r.Text, out int ri);
        int.TryParse(g.Text, out int gi);
        int.TryParse(b.Text, out int bi);
        // Preview clamps to 0-255 for display; the underlying value (possibly
        // negative or HDR) gets written to memory as-is on Apply.
        preview.Background = new SolidColorBrush(
            Color.FromRgb((byte)Math.Clamp(ri, 0, 255),
                          (byte)Math.Clamp(gi, 0, 255),
                          (byte)Math.Clamp(bi, 0, 255)));
    }

    private void BtnPickColor1_Click(object sender, RoutedEventArgs e)
        => OpenPickerFor(TxtColor1R, TxtColor1G, TxtColor1B);

    private void BtnPickColor2_Click(object sender, RoutedEventArgs e)
        => OpenPickerFor(TxtColor2R, TxtColor2G, TxtColor2B);

    private void OpenPickerFor(TextBox rBox, TextBox gBox, TextBox bBox)
    {
        var initial = new LinearColor();
        int.TryParse(rBox.Text, out int r); initial.R = r;
        int.TryParse(gBox.Text, out int g); initial.G = g;
        int.TryParse(bBox.Text, out int b); initial.B = b;

        var dlg = new ColorPickerDialog(initial);
        dlg.Owner = this;
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            rBox.Text = dlg.Result.R.ToString();
            gBox.Text = dlg.Result.G.ToString();
            bBox.Text = dlg.Result.B.ToString();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var v = new FieldValidator();

        // Empty textbox = keep baseline (= no change for that field).
        int heroStats        = IntOr(v, TxtHeroStats,        "Hero Stats",        _baseHeroStats);
        int towerStats       = IntOr(v, TxtTowerStats,       "Tower Stats",       _baseTowerStats);
        int resistances      = IntOr(v, TxtResistances,      "Resistances",       _baseResistances);
        int damage           = IntOr(v, TxtDamage,           "Damage",            _baseDamage);
        int rangedDamage     = IntOr(v, TxtRangedDamage,     "Ranged Damage",     _baseRangedDamage);
        int elementalDamage  = IntOr(v, TxtElementalDamage,  "Elemental Damage",  _baseElementalDamage);
        int blocking         = IntOr(v, TxtBlocking,         "Blocking",          _baseBlocking);
        int knockback        = IntOr(v, TxtKnockback,        "Knockback",         _baseKnockback);
        int chargeSpeed      = IntOr(v, TxtChargeSpeed,      "Charge Speed",      _baseChargeSpeed);
        int shotsPerSecond   = IntOr(v, TxtShotsPerSecond,   "Shots/Second",      _baseShotsPerSecond);
        int projectiles      = IntOr(v, TxtProjectiles,      "Projectiles",       _baseProjectiles);
        int projectileSpeed  = IntOr(v, TxtProjectileSpeed,  "Projectile Speed",  _baseProjectileSpeed);
        int clipAmmo         = IntOr(v, TxtClipAmmo,         "Clip Ammo",         _baseClipAmmo);
        int reloadSpeed      = IntOr(v, TxtReloadSpeed,      "Reload Speed",      _baseReloadSpeed);
        float drawScale      = FloatOr(v, TxtDrawScale,      "Draw Scale",        _baseDrawScale);
        float swingSpeed     = FloatOr(v, TxtSwingSpeed,     "Swing Speed",       _baseSwingSpeed);
        int level            = IntOr(v, TxtLevel,            "Level",             _baseLevel);
        int maxLevel         = IntOr(v, TxtMaxLevel,         "Max Level",         _baseMaxLevel);
        int storedMana       = IntOr(v, TxtStoredMana,       "Stored Mana",       _baseStoredMana);
        byte levelReq        = ByteOr(v, TxtLevelRequirement, "Level Requirement", _baseLevelRequirement);
        string description   = string.IsNullOrEmpty(TxtDescription.Text) ? (_baseDescription ?? string.Empty) : TxtDescription.Text;
        string forgerName    = string.IsNullOrEmpty(TxtForgerName.Text) ? (_baseForgerName ?? string.Empty) : TxtForgerName.Text;

        // Colors — use Int (not Byte) so DD1 HDR/negative values pass through.
        int c1r = v.Int(TxtColor1R, "Color 1 R");
        int c1g = v.Int(TxtColor1G, "Color 1 G");
        int c1b = v.Int(TxtColor1B, "Color 1 B");
        int c2r = v.Int(TxtColor2R, "Color 2 R");
        int c2g = v.Int(TxtColor2G, "Color 2 G");
        int c2b = v.Int(TxtColor2B, "Color 2 B");

        if (!v.IsValid)
        {
            Base.RaiseMessage(v.Report(), "Invalid Input");
            v.FocusFirstError();
            return;
        }

        // Diff: determine which fields changed.
        bool chHeroStats = heroStats != _baseHeroStats;
        bool chTowerStats = towerStats != _baseTowerStats;
        bool chResistances = resistances != _baseResistances;
        bool chDamage = damage != _baseDamage;
        bool chRangedDamage = rangedDamage != _baseRangedDamage;
        bool chElementalDamage = elementalDamage != _baseElementalDamage;
        bool chBlocking = blocking != _baseBlocking;
        bool chKnockback = knockback != _baseKnockback;
        bool chChargeSpeed = chargeSpeed != _baseChargeSpeed;
        bool chShotsPerSecond = shotsPerSecond != _baseShotsPerSecond;
        bool chProjectiles = projectiles != _baseProjectiles;
        bool chProjectileSpeed = projectileSpeed != _baseProjectileSpeed;
        bool chClipAmmo = clipAmmo != _baseClipAmmo;
        bool chReloadSpeed = reloadSpeed != _baseReloadSpeed;
        bool chDrawScale = Math.Abs(drawScale - _baseDrawScale) > 0.0001f;
        bool chSwingSpeed = Math.Abs(swingSpeed - _baseSwingSpeed) > 0.0001f;
        bool chDescription = description != (_baseDescription ?? string.Empty);
        bool chForgerName = forgerName != (_baseForgerName ?? string.Empty);
        bool chLevel = level != _baseLevel;
        bool chMaxLevel = maxLevel != _baseMaxLevel;
        bool chStoredMana = storedMana != _baseStoredMana;
        bool chLevelReq = levelReq != _baseLevelRequirement;
        // Colors are diffed as a whole card — if any channel changed we write
        // all three, so a tweak to just G doesn't accidentally reset R/B.
        bool chColor1 = c1r != _baseColor1R || c1g != _baseColor1G || c1b != _baseColor1B;
        bool chColor2 = c2r != _baseColor2R || c2g != _baseColor2G || c2b != _baseColor2B;

        // Count changed fields.
        int changedCount = 0;
        if (chHeroStats) changedCount++;
        if (chTowerStats) changedCount++;
        if (chResistances) changedCount++;
        if (chDamage) changedCount++;
        if (chRangedDamage) changedCount++;
        if (chElementalDamage) changedCount++;
        if (chBlocking) changedCount++;
        if (chKnockback) changedCount++;
        if (chChargeSpeed) changedCount++;
        if (chShotsPerSecond) changedCount++;
        if (chProjectiles) changedCount++;
        if (chProjectileSpeed) changedCount++;
        if (chClipAmmo) changedCount++;
        if (chReloadSpeed) changedCount++;
        if (chDrawScale) changedCount++;
        if (chSwingSpeed) changedCount++;
        if (chDescription) changedCount++;
        if (chForgerName) changedCount++;
        if (chLevel) changedCount++;
        if (chMaxLevel) changedCount++;
        if (chStoredMana) changedCount++;
        if (chLevelReq) changedCount++;
        if (chColor1) changedCount++;
        if (chColor2) changedCount++;

        if (changedCount == 0)
        {
            Base.RaiseMessage("No fields were changed.", "Bulk Edit");
            return;
        }

        var result = MessageBox.Show(
            $"Apply {changedCount} changed field(s) to {_addresses.Count} items?",
            "Confirm Bulk Edit",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        AppliedCount = 0;
        FailedCount = 0;

        foreach (int address in _addresses)
        {
            bool success = ApplyToItem(address,
                heroStats, towerStats, resistances,
                damage, rangedDamage, elementalDamage,
                blocking, knockback, chargeSpeed, shotsPerSecond,
                projectiles, projectileSpeed, clipAmmo, reloadSpeed,
                drawScale, swingSpeed,
                description, forgerName,
                level, maxLevel, storedMana, levelReq,
                c1r, c1g, c1b, c2r, c2g, c2b,
                chHeroStats, chTowerStats, chResistances,
                chDamage, chRangedDamage, chElementalDamage,
                chBlocking, chKnockback, chChargeSpeed, chShotsPerSecond,
                chProjectiles, chProjectileSpeed, chClipAmmo, chReloadSpeed,
                chDrawScale, chSwingSpeed,
                chDescription, chForgerName,
                chLevel, chMaxLevel, chStoredMana, chLevelReq,
                chColor1, chColor2);

            if (!success)
            {
                // Retry once on failure.
                success = ApplyToItem(address,
                    heroStats, towerStats, resistances,
                    damage, rangedDamage, elementalDamage,
                    blocking, knockback, chargeSpeed, shotsPerSecond,
                    projectiles, projectileSpeed, clipAmmo, reloadSpeed,
                    drawScale, swingSpeed,
                    description, forgerName,
                    level, maxLevel, storedMana, levelReq,
                    c1r, c1g, c1b, c2r, c2g, c2b,
                    chHeroStats, chTowerStats, chResistances,
                    chDamage, chRangedDamage, chElementalDamage,
                    chBlocking, chKnockback, chChargeSpeed, chShotsPerSecond,
                    chProjectiles, chProjectileSpeed, chClipAmmo, chReloadSpeed,
                    chDrawScale, chSwingSpeed,
                    chDescription, chForgerName,
                    chLevel, chMaxLevel, chStoredMana, chLevelReq,
                    chColor1, chColor2);
            }

            if (success)
                AppliedCount++;
            else
                FailedCount++;
        }

        if (FailedCount > 0)
        {
            Base.RaiseMessage(
                $"Bulk edit complete: {AppliedCount} succeeded, {FailedCount} failed.",
                "Bulk Edit");
        }

        DialogResult = true;
    }

    private bool ApplyToItem(int address,
        int heroStats, int towerStats, int resistances,
        int damage, int rangedDamage, int elementalDamage,
        int blocking, int knockback, int chargeSpeed, int shotsPerSecond,
        int projectiles, int projectileSpeed, int clipAmmo, int reloadSpeed,
        float drawScale, float swingSpeed,
        string description, string forgerName,
        int level, int maxLevel, int storedMana, byte levelReq,
        int c1r, int c1g, int c1b, int c2r, int c2g, int c2b,
        bool chHeroStats, bool chTowerStats, bool chResistances,
        bool chDamage, bool chRangedDamage, bool chElementalDamage,
        bool chBlocking, bool chKnockback, bool chChargeSpeed, bool chShotsPerSecond,
        bool chProjectiles, bool chProjectileSpeed, bool chClipAmmo, bool chReloadSpeed,
        bool chDrawScale, bool chSwingSpeed,
        bool chDescription, bool chForgerName,
        bool chLevel, bool chMaxLevel, bool chStoredMana, bool chLevelReq,
        bool chColor1, bool chColor2)
    {
        try
        {
            // Read current item from memory.
            byte[] raw = Base.Instance.ReadMemory(address, _structSize);
            ItemNative item = Base.Push<ItemNative>(raw);

            // Only overwrite changed fields.
            if (chHeroStats)
            {
                for (int i = 0; i <= 5; i++)
                    item.StatModifiers[i] = heroStats;
            }

            if (chTowerStats)
            {
                for (int i = 6; i <= 9; i++)
                    item.StatModifiers[i] = towerStats;
            }

            if (chResistances)
            {
                for (int i = 0; i <= 3; i++)
                    item.DamageReductions[i].Value = resistances;
            }

            if (chDamage) item.WeaponDamageBonus = damage;
            if (chRangedDamage) item.WeaponAltDamageBonus = rangedDamage;
            if (chElementalDamage) item.WeaponAdditionalDamage.Value = elementalDamage;
            if (chBlocking) item.WeaponBlockingBonus = blocking;
            if (chKnockback) item.WeaponKnockbackBonus = knockback;
            if (chChargeSpeed) item.WeaponChargeSpeedBonus = chargeSpeed;
            if (chShotsPerSecond) item.WeaponShotsPerSecondBonus = shotsPerSecond;
            if (chProjectiles) item.WeaponNumberOfProjectilesBonus = projectiles;
            if (chProjectileSpeed) item.WeaponSpeedOfProjectilesBonus = projectileSpeed;
            if (chClipAmmo) item.WeaponClipAmmoBonus = clipAmmo;
            if (chReloadSpeed) item.WeaponReloadSpeedBonus = reloadSpeed;
            if (chDrawScale) item.WeaponDrawScaleMultiplier = drawScale;
            if (chSwingSpeed) item.WeaponSwingSpeedMultiplier = swingSpeed;
            if (chLevel) item.Level = level;
            if (chMaxLevel) item.MaxEquipmentLevel = maxLevel;
            if (chStoredMana) item.StoredMana = storedMana;
            if (chLevelReq) item.ManualLR = levelReq;

            // Color overrides — build a LinearColor (float-backed) from the
            // ints and convert to native. Negative values pass straight through
            // because LinearColor.R setter is value/255f (not clamped).
            if (chColor1)
            {
                var c = new LinearColor { R = c1r, G = c1g, B = c1b };
                item.PrimaryColorOverride = Base.LinearColorToNative(c);
            }
            if (chColor2)
            {
                var c = new LinearColor { R = c2r, G = c2g, B = c2b };
                item.SecondaryColorOverride = Base.LinearColorToNative(c);
            }

            // Handle string fields: try in-place first, fall back to new allocation.
            if (chDescription)
            {
                if (item.Description.MaximumLength >= description.Length + 1)
                    item.Description = Base.WriteUniInPlace(item.Description, description);
                else
                    item.Description = Base.WriteUni(address, "Description", description);
            }

            if (chForgerName)
            {
                if (item.ForgerName.MaximumLength >= forgerName.Length + 1)
                    item.ForgerName = Base.WriteUniInPlace(item.ForgerName, forgerName);
                else
                    item.ForgerName = Base.WriteUni(address, "ForgerName", forgerName);
            }

            // Write back to memory.
            byte[] data = Base.Push(item);
            Base.Instance.WriteMemory(address, data);

            // Verify by re-reading.
            byte[] verify = Base.Instance.ReadMemory(address, _structSize);
            ItemNative verifyItem = Base.Push<ItemNative>(verify);

            if (chLevel && verifyItem.Level != level) return false;
            if (chDamage && verifyItem.WeaponDamageBonus != damage) return false;
            if (chHeroStats && verifyItem.StatModifiers[0] != heroStats) return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Placeholder + fallback helpers ──────────────────────────────

    // Blank the TextBox and stuff the current value into the grey placeholder.
    private static void SetHint(TextBox tb, string current)
    {
        tb.Text = "";
        Modinator.Behaviors.Placeholder.SetText(tb, current);
    }

    // If the user typed something, parse it; otherwise keep the baseline value.
    private static int IntOr(FieldValidator v, TextBox tb, string label, int fallback)
        => string.IsNullOrWhiteSpace(tb.Text) ? fallback : v.Int(tb, label);
    private static byte ByteOr(FieldValidator v, TextBox tb, string label, byte fallback)
        => string.IsNullOrWhiteSpace(tb.Text) ? fallback : v.Byte(tb, label);
    private static float FloatOr(FieldValidator v, TextBox tb, string label, float fallback)
        => string.IsNullOrWhiteSpace(tb.Text) ? fallback : v.Float(tb, label);

    // ── MAX bulk ────────────────────────────────────────────────────
    //
    // Applies the shared MaxItemConfig to every selected item, re-evaluating
    // each item's current state individually (so an armor piece and a weapon
    // in the same selection get different rules applied). Numeric fields flow
    // through MaxItemConfig.ApplyTo; strings are handled here with the same
    // WriteUniInPlace-with-fallback pattern the APPLY path uses.

    private void BtnMaxConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new MaxItemConfigDialog(MaxItemConfig.Load());
        dlg.Owner = this;
        dlg.ShowDialog();
    }

    private void BtnMax_Click(object sender, RoutedEventArgs e)
    {
        var cfg = MaxItemConfig.Load();

        var confirm = MessageBox.Show(
            $"Apply MAX config to {_addresses.Count} items? Each item is evaluated individually.",
            "Confirm MAX",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        AppliedCount = 0;
        FailedCount = 0;
        var failures = new List<(int addr, string step, string err)>();

        foreach (int address in _addresses)
        {
            var (ok, step, err) = ApplyMaxToAddress(address, cfg);
            if (!ok)
            {
                // Retry once on failure — silently; only log the retry result.
                (ok, step, err) = ApplyMaxToAddress(address, cfg);
            }
            if (ok) AppliedCount++;
            else { FailedCount++; failures.Add((address, step, err ?? "(unknown)")); }
        }

        string summary = $"MAX complete: {AppliedCount} succeeded, {FailedCount} failed.";
        if (failures.Count > 0)
        {
            string logPath = WriteFailureLog(cfg, failures);
            summary += $"\n\nFailure log:\n{logPath}";

            // Summarize by step so the user immediately sees the common cause.
            var byStep = failures.GroupBy(f => f.step)
                                 .OrderByDescending(g => g.Count())
                                 .Select(g => $"  {g.Count()} @ {g.Key}");
            summary += "\n\nBreakdown:\n" + string.Join("\n", byStep);
        }
        Base.RaiseMessage(summary, "MAX");
        DialogResult = true;
    }

    // Returns (success, step-that-failed, error-message). On success step/err are empty.
    private (bool ok, string step, string? err) ApplyMaxToAddress(int address, MaxItemConfig cfg)
    {
        byte[] raw;
        try
        {
            raw = Base.Instance.ReadMemory(address, _structSize);
            if (raw == null || raw.Length < _structSize)
                return (false, "ReadMemory", $"returned {raw?.Length ?? 0} bytes, expected {_structSize}");
        }
        catch (Exception ex) { return (false, "ReadMemory", ex.GetType().Name + ": " + ex.Message); }

        ItemNative item;
        try { item = Base.Push<ItemNative>(raw); }
        catch (Exception ex) { return (false, "Push<ItemNative>", ex.GetType().Name + ": " + ex.Message); }

        // weaponType (EWeaponType) lives outside the marshaled ItemNative
        // (object+0x824 = address + MaxCompat.WeaponTypeOffset). Read the
        // byte so ApplyTo can be class-aware; harmless/ignored for non-weapons.
        byte weaponType = 0;
        try
        {
            byte[]? wb = Base.Instance.ReadMemory(address + MaxCompat.WeaponTypeOffset, 1);
            if (wb != null && wb.Length > 0) weaponType = wb[0];
        }
        catch { }

        try { item = cfg.ApplyTo(item, weaponType); }
        catch (Exception ex) { return (false, "ApplyTo", ex.GetType().Name + ": " + ex.Message); }

        // Strings: prefer in-place write (safest); fall back to fresh alloc
        // when the existing buffer is too small (common for ForgerName since
        // unforged items have a zero-length buffer). A fallback failure is
        // non-fatal — the numeric maxes still get written so the item isn't
        // a total loss. The outer try still catches unexpected exceptions.
        try
        {
            if (!string.IsNullOrEmpty(cfg.Description))
                item.Description = WriteStringBestEffort(item.Description, cfg.Description, address, "Description");
            if (!string.IsNullOrEmpty(cfg.ForgerName))
                item.ForgerName = WriteStringBestEffort(item.ForgerName, cfg.ForgerName, address, "ForgerName");
        }
        catch (Exception ex) { return (false, "Strings", ex.GetType().Name + ": " + ex.Message); }

        byte[] data;
        try { data = Base.Push(item); }
        catch (Exception ex) { return (false, "Push(item)", ex.GetType().Name + ": " + ex.Message); }

        try { Base.Instance.WriteMemory(address, data); }
        catch (Exception ex) { return (false, "WriteMemory", ex.GetType().Name + ": " + ex.Message); }

        return (true, "", null);
    }

    // Try in-place first (zero risk), then fall back to a fresh allocation.
    // If the alloc throws (VirtualAllocEx occasionally fails during bulk
    // edits), swallow it and keep the existing NativeArray — the numeric
    // writes for this item still succeed.
    private static NativeArray WriteStringBestEffort(NativeArray existing, string data, int address, string fieldName)
    {
        if (existing.MaximumLength >= data.Length + 1)
            return Base.WriteUniInPlace(existing, data);
        try
        {
            return Base.WriteUni(address, fieldName, data);
        }
        catch
        {
            return existing;
        }
    }

    private static string WriteFailureLog(MaxItemConfig cfg, List<(int addr, string step, string err)> failures)
    {
        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Modinator");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(
            dir, $"bulk_max_failures_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Bulk MAX failure log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Failures: {failures.Count}");
        sb.AppendLine();
        sb.AppendLine("Config:");
        sb.AppendLine(System.Text.Json.JsonSerializer.Serialize(cfg, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine();
        sb.AppendLine("Per-address failures:");
        foreach (var f in failures)
            sb.AppendLine($"  0x{f.addr:X8}  step={f.step}  err={f.err}");

        try { System.IO.File.WriteAllText(path, sb.ToString()); } catch { }
        return path;
    }
}
