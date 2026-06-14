using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static Modinator.Views.EditHelpers;

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

        // A stale first selection (item moved/unloaded since the scan) used
        // to throw out of the constructor and crash the dialog open \u2014 fail
        // soft instead: explain and close. _loadFailed also hard-gates
        // APPLY/MAX, so even if the close races the dialog becoming visible,
        // nothing can be written against the zeroed baselines.
        if (!LoadBaseline())
        {
            _loadFailed = true;
            Base.RaiseMessage(
                "Couldn't read the first selected item \u2014 it may have moved or " +
                "unloaded since the scan. Rescan and try again.",
                "Bulk Edit");
            Loaded += (s, e) => Close();
        }
    }

    private bool _loadFailed;

    // Items whose Description/ForgerName allocation failed during the last
    // bulk pass (numerics still applied \u2014 see WriteStringBestEffort).
    private int _stringWriteFailures;

    private bool LoadBaseline()
    {
        int address = _addresses[0];

        byte[] raw;
        ItemNative baseline;
        try
        {
            raw = Base.Instance.ReadMemory(address, _structSize);
            if (raw == null || raw.Length < _structSize) return false;
            baseline = Base.Push<ItemNative>(raw);
        }
        catch { return false; }

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
        return true;
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

    // One bulk-apply pass captured from the form: each field's target value
    // plus its "actually changed vs baseline" flag. The write semantics are
    // identical to the original per-parameter version — this just gives the
    // ~50 values one home so apply/retry don't thread them all twice.
    private sealed class BulkPlan
    {
        public int HeroStats, TowerStats, Resistances;
        public int Damage, RangedDamage, ElementalDamage;
        public int Blocking, Knockback, ChargeSpeed, ShotsPerSecond;
        public int Projectiles, ProjectileSpeed, ClipAmmo, ReloadSpeed;
        public float DrawScale, SwingSpeed;
        public string Description = "", ForgerName = "";
        public int Level, MaxLevel, StoredMana;
        public byte LevelReq;
        public int C1R, C1G, C1B, C2R, C2G, C2B;

        public bool ChHeroStats, ChTowerStats, ChResistances;
        public bool ChDamage, ChRangedDamage, ChElementalDamage;
        public bool ChBlocking, ChKnockback, ChChargeSpeed, ChShotsPerSecond;
        public bool ChProjectiles, ChProjectileSpeed, ChClipAmmo, ChReloadSpeed;
        public bool ChDrawScale, ChSwingSpeed;
        public bool ChDescription, ChForgerName;
        public bool ChLevel, ChMaxLevel, ChStoredMana, ChLevelReq;
        public bool ChColor1, ChColor2;

        public int ChangedCount => new[]
        {
            ChHeroStats, ChTowerStats, ChResistances,
            ChDamage, ChRangedDamage, ChElementalDamage,
            ChBlocking, ChKnockback, ChChargeSpeed, ChShotsPerSecond,
            ChProjectiles, ChProjectileSpeed, ChClipAmmo, ChReloadSpeed,
            ChDrawScale, ChSwingSpeed,
            ChDescription, ChForgerName,
            ChLevel, ChMaxLevel, ChStoredMana, ChLevelReq,
            ChColor1, ChColor2,
        }.Count(c => c);
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_loadFailed) return; // baselines are zeroed — dialog is closing
        var v = new FieldValidator();
        var p = new BulkPlan();

        // Empty textbox = keep baseline (= no change for that field).
        p.HeroStats        = IntOr(v, TxtHeroStats,        "Hero Stats",        _baseHeroStats);
        p.TowerStats       = IntOr(v, TxtTowerStats,       "Tower Stats",       _baseTowerStats);
        p.Resistances      = IntOr(v, TxtResistances,      "Resistances",       _baseResistances);
        p.Damage           = IntOr(v, TxtDamage,           "Damage",            _baseDamage);
        p.RangedDamage     = IntOr(v, TxtRangedDamage,     "Ranged Damage",     _baseRangedDamage);
        p.ElementalDamage  = IntOr(v, TxtElementalDamage,  "Elemental Damage",  _baseElementalDamage);
        p.Blocking         = IntOr(v, TxtBlocking,         "Blocking",          _baseBlocking);
        p.Knockback        = IntOr(v, TxtKnockback,        "Knockback",         _baseKnockback);
        p.ChargeSpeed      = IntOr(v, TxtChargeSpeed,      "Charge Speed",      _baseChargeSpeed);
        p.ShotsPerSecond   = IntOr(v, TxtShotsPerSecond,   "Shots/Second",      _baseShotsPerSecond);
        p.Projectiles      = IntOr(v, TxtProjectiles,      "Projectiles",       _baseProjectiles);
        p.ProjectileSpeed  = IntOr(v, TxtProjectileSpeed,  "Projectile Speed",  _baseProjectileSpeed);
        p.ClipAmmo         = IntOr(v, TxtClipAmmo,         "Clip Ammo",         _baseClipAmmo);
        p.ReloadSpeed      = IntOr(v, TxtReloadSpeed,      "Reload Speed",      _baseReloadSpeed);
        p.DrawScale        = FloatOr(v, TxtDrawScale,      "Draw Scale",        _baseDrawScale);
        p.SwingSpeed       = FloatOr(v, TxtSwingSpeed,     "Swing Speed",       _baseSwingSpeed);
        p.Level            = IntOr(v, TxtLevel,            "Level",             _baseLevel);
        p.MaxLevel         = IntOr(v, TxtMaxLevel,         "Max Level",         _baseMaxLevel);
        p.StoredMana       = IntOr(v, TxtStoredMana,       "Stored Mana",       _baseStoredMana);
        p.LevelReq         = ByteOr(v, TxtLevelRequirement, "Level Requirement", _baseLevelRequirement);
        p.Description      = StrOr(TxtDescription, _baseDescription);
        p.ForgerName       = StrOr(TxtForgerName, _baseForgerName);

        // Colors — use Int (not Byte) so DD1 HDR/negative values pass through.
        p.C1R = v.Int(TxtColor1R, "Color 1 R");
        p.C1G = v.Int(TxtColor1G, "Color 1 G");
        p.C1B = v.Int(TxtColor1B, "Color 1 B");
        p.C2R = v.Int(TxtColor2R, "Color 2 R");
        p.C2G = v.Int(TxtColor2G, "Color 2 G");
        p.C2B = v.Int(TxtColor2B, "Color 2 B");

        if (!v.IsValid)
        {
            Base.RaiseMessage(v.Report(), "Invalid Input");
            v.FocusFirstError();
            return;
        }

        // Diff: determine which fields changed vs the baseline.
        p.ChHeroStats       = p.HeroStats != _baseHeroStats;
        p.ChTowerStats      = p.TowerStats != _baseTowerStats;
        p.ChResistances     = p.Resistances != _baseResistances;
        p.ChDamage          = p.Damage != _baseDamage;
        p.ChRangedDamage    = p.RangedDamage != _baseRangedDamage;
        p.ChElementalDamage = p.ElementalDamage != _baseElementalDamage;
        p.ChBlocking        = p.Blocking != _baseBlocking;
        p.ChKnockback       = p.Knockback != _baseKnockback;
        p.ChChargeSpeed     = p.ChargeSpeed != _baseChargeSpeed;
        p.ChShotsPerSecond  = p.ShotsPerSecond != _baseShotsPerSecond;
        p.ChProjectiles     = p.Projectiles != _baseProjectiles;
        p.ChProjectileSpeed = p.ProjectileSpeed != _baseProjectileSpeed;
        p.ChClipAmmo        = p.ClipAmmo != _baseClipAmmo;
        p.ChReloadSpeed     = p.ReloadSpeed != _baseReloadSpeed;
        p.ChDrawScale       = Math.Abs(p.DrawScale - _baseDrawScale) > 0.0001f;
        p.ChSwingSpeed      = Math.Abs(p.SwingSpeed - _baseSwingSpeed) > 0.0001f;
        p.ChDescription     = p.Description != (_baseDescription ?? string.Empty);
        p.ChForgerName      = p.ForgerName != (_baseForgerName ?? string.Empty);
        p.ChLevel           = p.Level != _baseLevel;
        p.ChMaxLevel        = p.MaxLevel != _baseMaxLevel;
        p.ChStoredMana      = p.StoredMana != _baseStoredMana;
        p.ChLevelReq        = p.LevelReq != _baseLevelRequirement;
        // Colors are diffed as a whole card — if any channel changed we write
        // all three, so a tweak to just G doesn't accidentally reset R/B.
        p.ChColor1 = p.C1R != _baseColor1R || p.C1G != _baseColor1G || p.C1B != _baseColor1B;
        p.ChColor2 = p.C2R != _baseColor2R || p.C2G != _baseColor2G || p.C2B != _baseColor2B;

        int changedCount = p.ChangedCount;
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
        _stringWriteFailures = 0;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            foreach (int address in _addresses)
            {
                // Retry once — a target can transiently fail mid-game-tick.
                bool success = ApplyToItem(address, p) || ApplyToItem(address, p);
                if (success) AppliedCount++;
                else FailedCount++;
            }
        }
        finally { Mouse.OverrideCursor = null; }

        if (FailedCount > 0 || _stringWriteFailures > 0)
        {
            string msg = $"Bulk edit complete: {AppliedCount} succeeded, {FailedCount} failed.";
            if (_stringWriteFailures > 0)
                msg += $"\n\n{_stringWriteFailures} item(s) kept their old name/description " +
                       "(game memory allocation failed) — their numeric stats were still " +
                       "applied. Re-apply to retry the text.";
            Base.RaiseMessage(msg, "Bulk Edit");
        }

        DialogResult = true;
    }

    private bool ApplyToItem(int address, BulkPlan p)
    {
        try
        {
            // Read current item from memory.
            byte[] raw = Base.Instance.ReadMemory(address, _structSize);
            ItemNative item = Base.Push<ItemNative>(raw);

            // Only overwrite changed fields.
            if (p.ChHeroStats)
            {
                for (int i = 0; i <= 5; i++)
                    item.StatModifiers[i] = p.HeroStats;
            }

            if (p.ChTowerStats)
            {
                for (int i = 6; i <= 9; i++)
                    item.StatModifiers[i] = p.TowerStats;
            }

            if (p.ChResistances)
            {
                for (int i = 0; i <= 3; i++)
                    item.DamageReductions[i].Value = p.Resistances;
            }

            if (p.ChDamage) item.WeaponDamageBonus = p.Damage;
            if (p.ChRangedDamage) item.WeaponAltDamageBonus = p.RangedDamage;
            if (p.ChElementalDamage) item.WeaponAdditionalDamage.Value = p.ElementalDamage;
            if (p.ChBlocking) item.WeaponBlockingBonus = p.Blocking;
            if (p.ChKnockback) item.WeaponKnockbackBonus = p.Knockback;
            if (p.ChChargeSpeed) item.WeaponChargeSpeedBonus = p.ChargeSpeed;
            if (p.ChShotsPerSecond) item.WeaponShotsPerSecondBonus = p.ShotsPerSecond;
            if (p.ChProjectiles) item.WeaponNumberOfProjectilesBonus = p.Projectiles;
            if (p.ChProjectileSpeed) item.WeaponSpeedOfProjectilesBonus = p.ProjectileSpeed;
            if (p.ChClipAmmo) item.WeaponClipAmmoBonus = p.ClipAmmo;
            if (p.ChReloadSpeed) item.WeaponReloadSpeedBonus = p.ReloadSpeed;
            if (p.ChDrawScale) item.WeaponDrawScaleMultiplier = p.DrawScale;
            if (p.ChSwingSpeed) item.WeaponSwingSpeedMultiplier = p.SwingSpeed;
            if (p.ChLevel) item.Level = p.Level;
            if (p.ChMaxLevel) item.MaxEquipmentLevel = p.MaxLevel;
            if (p.ChStoredMana) item.StoredMana = p.StoredMana;
            if (p.ChLevelReq) item.ManualLR = p.LevelReq;

            // Color overrides — build a LinearColor (float-backed) from the
            // ints and convert to native. Negative values pass straight through
            // because LinearColor.R setter is value/255f (not clamped).
            if (p.ChColor1)
            {
                var c = new LinearColor { R = p.C1R, G = p.C1G, B = p.C1B };
                item.PrimaryColorOverride = Base.LinearColorToNative(c);
            }
            if (p.ChColor2)
            {
                var c = new LinearColor { R = p.C2R, G = p.C2G, B = p.C2B };
                item.SecondaryColorOverride = Base.LinearColorToNative(c);
            }

            // Strings: in-place first, fresh allocation as fallback, and a
            // failed allocation keeps the existing buffer (same best-effort
            // contract as the MAX path) — the numeric writes below still
            // land instead of the whole item counting as failed.
            if (p.ChDescription)
                item.Description = WriteStringBestEffort(item.Description, p.Description, address, "Description");
            if (p.ChForgerName)
                item.ForgerName = WriteStringBestEffort(item.ForgerName, p.ForgerName, address, "ForgerName");

            // Write back to memory.
            byte[] data = Base.Push(item);
            Base.Instance.WriteMemory(address, data);

            // Verify by re-reading.
            byte[] verify = Base.Instance.ReadMemory(address, _structSize);
            ItemNative verifyItem = Base.Push<ItemNative>(verify);

            if (p.ChLevel && verifyItem.Level != p.Level) return false;
            if (p.ChDamage && verifyItem.WeaponDamageBonus != p.Damage) return false;
            if (p.ChHeroStats && verifyItem.StatModifiers[0] != p.HeroStats) return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

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
        if (_loadFailed) return; // baselines are zeroed — dialog is closing
        var cfg = MaxItemConfig.Load();

        var confirm = MessageBox.Show(
            $"Apply MAX config to {_addresses.Count} items? Each item is evaluated individually.",
            "Confirm MAX",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        AppliedCount = 0;
        FailedCount = 0;
        _stringWriteFailures = 0;
        var failures = new List<(int addr, string step, string err)>();

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
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
        }
        finally { Mouse.OverrideCursor = null; }

        string summary = $"MAX complete: {AppliedCount} succeeded, {FailedCount} failed.";
        if (_stringWriteFailures > 0)
            summary += $"\n{_stringWriteFailures} item(s) kept their old name/description " +
                       "(allocation failed); numeric maxes still applied.";
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
    // writes for this item still succeed. Counted into
    // _stringWriteFailures so the summary can say so instead of silently
    // reporting full success.
    private NativeArray WriteStringBestEffort(NativeArray existing, string data, int address, string fieldName)
    {
        if (existing.MaximumLength >= data.Length + 1)
            return Base.WriteUniInPlace(existing, data);
        try
        {
            return Base.WriteUni(address, fieldName, data);
        }
        catch
        {
            _stringWriteFailures++;
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
