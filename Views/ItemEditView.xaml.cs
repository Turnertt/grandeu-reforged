using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using static Modinator.Views.EditHelpers;

namespace Modinator.Views;

public partial class ItemEditView : UserControl
{
    private int Address;
    private string ItemDisplayName;
    private ItemNative _lastNative;
    // Colour values the boxes were seeded with on the last Refresh, so UPDATE
    // can tell an untouched colour from an edited one.
    private (int R, int G, int B) _seedC1, _seedC2;
    // Identity of the item as of the last Refresh. Every write re-reads the
    // address and refuses if a different item (or freed/reused memory) is
    // there now — see ItemIdentity. REFRESH re-captures, so a user who has
    // seen the new contents can edit them knowingly.
    private ItemIdentity _openedIdentity;
    private bool _hasIdentity;

    // Re-read the item and confirm it is still the one this screen loaded.
    // Returns false (and explains in the status pill) when it is not.
    private bool VerifyStillSameItem(out ItemNative live)
    {
        int size = Marshal.SizeOf(typeof(ItemNative));
        live = Base.Push<ItemNative>(Base.Instance.ReadMemory(Address, size));
        if (!_hasIdentity || _openedIdentity.Matches(live)) return true;
        StatusText.Text = "Not written — " + ItemIdentity.ChangedMessage;
        return false;
    }

    public ItemEditView(int address, string name)
    {
        InitializeComponent();
        Address = address;
        ItemDisplayName = string.IsNullOrWhiteSpace(name) ? "Item" : name;
        StatusText.Text = Base.Truncate(ItemDisplayName);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Populate enum combos
        CboEquipmentType.Items.Clear();
        foreach (var val in Enum.GetValues(typeof(EquipmentType)))
            CboEquipmentType.Items.Add(val);

        CboQuality2.Items.Clear();
        foreach (Quality2 val in Enum.GetValues(typeof(Quality2)))
            CboQuality2.Items.Add(new Quality2Choice(val));

        CboQuality3.Items.Clear();
        foreach (var val in Enum.GetValues(typeof(Quality3)))
            CboQuality3.Items.Add(val);

        TxtMemoryAddress.Text = "0x" + Address.ToString("X8");

        Refresh();
    }

    private void Refresh()
    {
        try
        {
            int size = Marshal.SizeOf(typeof(ItemNative));
            byte[] data = Base.Instance.ReadMemory(Address, size);
            ItemNative native = Base.Push<ItemNative>(data);
            _lastNative = native;
            _openedIdentity = ItemIdentity.Of(native);
            _hasIdentity = true;
            ItemUser user = Base.ItemToUser(native);

            // Read string fields
            string itemName = Base.ReadUni<ItemNative>(Address, "EquipmentName");
            string description = Base.ReadUni<ItemNative>(Address, "Description");
            string forgerName = Base.ReadUni<ItemNative>(Address, "ForgerName");
            _storedForgerName = forgerName ?? "";

            // Numeric / string fields: show current value as grey hint, leave
            // textbox empty. On save, empty = keep as-is, so the user can type
            // without clearing. Color R/G/B and combo boxes stay populated.

            // Hero Stats
            SetHint(TxtHeroHealth, user.HeroHealth.ToString());
            SetHint(TxtHeroSpeed, user.HeroSpeed.ToString());
            SetHint(TxtHeroDamage, user.HeroDamage.ToString());
            SetHint(TxtHeroCasting, user.HeroCasting.ToString());
            SetHint(TxtHeroSkill1, user.HeroSkill1.ToString());
            SetHint(TxtHeroSkill2, user.HeroSkill2.ToString());

            // Tower Stats
            SetHint(TxtTowerHealth, user.TowerHealth.ToString());
            SetHint(TxtTowerSpeed, user.TowerSpeed.ToString());
            SetHint(TxtTowerDamage, user.TowerDamage.ToString());
            SetHint(TxtTowerRange, user.TowerRange.ToString());

            // Weapon
            SetHint(TxtDamage, user.Damage.ToString());
            SetHint(TxtRangedDamage, user.RangedDamage.ToString());
            SetHint(TxtBlocking, user.Blocking.ToString());
            SetHint(TxtKnockback, user.Knockback.ToString());
            SetHint(TxtChargeSpeed, user.ChargeSpeed.ToString());
            SetHint(TxtShotsPerSecond, user.ShotsPerSecond.ToString());
            SetHint(TxtNumProjectiles, user.NumberOfProjectiles.ToString());
            SetHint(TxtSpeedOfProjectiles, user.SpeedOfProjectiles.ToString());
            SetHint(TxtClipAmmo, user.ClipAmmo.ToString());
            SetHint(TxtReloadSpeed, user.ReloadSpeed.ToString());
            SetHint(TxtDrawScale, user.DrawScale.ToString("G", CultureInfo.InvariantCulture));
            SetHint(TxtSwingSpeed, user.SwingSpeed.ToString("G", CultureInfo.InvariantCulture));

            // Resistance
            SetHint(TxtGeneric, user.Generic?.Value.ToString() ?? "0");
            SetHint(TxtPoison, user.Poison?.Value.ToString() ?? "0");
            SetHint(TxtFire, user.Fire?.Value.ToString() ?? "0");
            SetHint(TxtLightning, user.Lightning?.Value.ToString() ?? "0");

            // Elemental Damage
            SetHint(TxtElementalDamage, user.ElementalDamage?.Value.ToString() ?? "0");
            SetHint(TxtElementalType, _lastNative.WeaponAdditionalDamage.DamageType.ToString("X"));

            // Quality
            SetHint(TxtQuality1, user.Quality1.ToString());
            foreach (Quality2Choice c in CboQuality2.Items)
                if (c.Value == user.Quality2) { CboQuality2.SelectedItem = c; break; }
            CboQuality3.SelectedItem = user.Quality3;
            SetHint(TxtQualityFlag, user.QualityFlag.ToString());

            // Colors — stay populated so live previews work.
            if (user.Color1Override != null)
            {
                TxtColor1R.Text = user.Color1Override.R.ToString();
                TxtColor1G.Text = user.Color1Override.G.ToString();
                TxtColor1B.Text = user.Color1Override.B.ToString();
                _seedC1 = (user.Color1Override.R, user.Color1Override.G, user.Color1Override.B);
            }
            if (user.Color2Override != null)
            {
                TxtColor2R.Text = user.Color2Override.R.ToString();
                TxtColor2G.Text = user.Color2Override.G.ToString();
                TxtColor2B.Text = user.Color2Override.B.ToString();
                _seedC2 = (user.Color2Override.R, user.Color2Override.G, user.Color2Override.B);
            }

            // Identity
            SetHint(TxtItemName, itemName ?? "");
            // Hint only — colour runs stripped so the watermark doesn't show
            // as tag soup. Leaving the box blank still writes the raw text
            // (BtnUpdate_Click re-reads it), so nothing is lost.
            // Stored line breaks surface as a literal \n so they can be seen and
            // re-typed in a single-line box; the write path turns them back.
            SetHint(TxtDescription, Watermark.StripColorTags(description).Replace("\n", "\\n"));
            // Stripped for the same reason as Description: a coloured forger
            // name would otherwise show as raw tags. Leaving the box blank
            // still preserves the stored text verbatim.
            SetHint(TxtForgerName, ColorMarkup.Strip(forgerName));
            UpdateForgerWarning();
            CboEquipmentType.SelectedItem = user.EquipmentType;
            SetHint(TxtEquipmentTemplate, user.EquipmentTemplate ?? "");

            // Weapon class (EWeaponType) — read-only. Lives outside the
            // marshaled ItemNative (Address + MaxCompat.WeaponTypeOffset);
            // only meaningful for weapons. Empty catch per the memory-read
            // convention — a failed read just shows the dash.
            if (user.EquipmentType == EquipmentType.Weapon)
            {
                string cls = "—";
                try
                {
                    byte[]? wb = Base.Instance.ReadMemory(Address + MaxCompat.WeaponTypeOffset, 1);
                    if (wb != null && wb.Length > 0) cls = WeaponClass.Name(wb[0]);
                }
                catch { }
                TxtWeaponClass.Text = cls;
            }
            else
            {
                TxtWeaponClass.Text = "—";
            }

            // Economy
            SetHint(TxtMaxValue, user.MaximumValue.ToString());
            SetHint(TxtMinValue, user.MinimumValue.ToString());
            SetHint(TxtRating, user.Rating.ToString("G", CultureInfo.InvariantCulture));
            SetHint(TxtRatingPercent, user.RatingPercent.ToString("G", CultureInfo.InvariantCulture));

            // Level
            SetHint(TxtLevel, user.Level.ToString());
            SetHint(TxtMaxLevel, user.MaxLevel.ToString());
            SetHint(TxtStoredMana, user.StoredMana.ToString());
            SetHint(TxtLevelRequirement, user.LevelRequirement.ToString());
            SetHint(TxtID1, user.ID1.ToString());
            SetHint(TxtID2, user.ID2.ToString());

            // Keep the item's name visible — the user should always see
            // WHICH item this screen is editing.
            StatusText.Text = $"{Base.Truncate(ItemDisplayName)} — refreshed";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Read error: " + ex.Message;
        }
    }

    // Try in-place write; fall back to a fresh allocation if the string is
    // longer than the existing buffer. WriteUniInPlace silently bails when
    // the buffer is too small, so without this the UI appears to succeed
    // but memory never changes.
    // ── Colour editor + forger-name warning ─────────────────────────

    // The item's stored forger name, so the warning can reflect "this item is
    // already unsellable" and not just "you are typing one right now".
    private string _storedForgerName = "";

    private void BtnColorDescription_Click(object sender, RoutedEventArgs e)
        => OpenColorEditor(TxtDescription, "Description", "Description", isForgerName: false);

    private void BtnColorForger_Click(object sender, RoutedEventArgs e)
        => OpenColorEditor(TxtForgerName, "ForgerName", "Forger Name", isForgerName: true);

    private void OpenColorEditor(TextBox box, string field, string title, bool isForgerName)
    {
        string current = box.Text ?? "";
        if (current.Length == 0)
        {
            // An empty box means "keep what's stored" (see StrOr in the update
            // path), so the editor has to start from the item's real text —
            // tags and all — rather than from blank.
            try { current = Base.ReadUni<ItemNative>(Address, field) ?? ""; }
            catch { current = ""; }
        }

        var dlg = new ColorTextDialog(current, title, isForgerName)
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true) return;

        // Stage into the textbox rather than writing to memory here: UPDATE
        // stays the single write path, so nothing lands on the item behind
        // the user's back and CANCEL on the editor really cancels.
        // Stage line breaks as the literal escape the hint uses, so what sits
        // in this single-line box is visible and re-typeable; the write path
        // (NormalizeNewlines) turns them back into real newlines.
        box.Text = dlg.ResultMarkup.Replace("\n", "\\n");
        StatusText.Text = "Colors staged — press UPDATE to write them to the item.";
    }

    private void TxtForgerName_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateForgerWarning();

    private void UpdateForgerWarning()
    {
        // Can fire from InitializeComponent before the label exists.
        if (LblForgerWarn == null) return;
        string typed = TxtForgerName.Text ?? "";
        // Empty box keeps the stored name, so that is what decides it.
        bool willHaveForger = typed.Length > 0
            ? !string.IsNullOrWhiteSpace(ColorMarkup.Strip(typed))
            : !string.IsNullOrWhiteSpace(ColorMarkup.Strip(_storedForgerName));
        LblForgerWarn.Visibility = willHaveForger ? Visibility.Visible : Visibility.Collapsed;
    }

    private NativeArray WriteStringWithFallback(NativeArray existing, string data, string fieldName)
    {
        if (existing.MaximumLength >= data.Length + 1)
            return Base.WriteUniInPlace(existing, data);
        return Base.WriteUni(Address, fieldName, data);
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        Refresh();
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            main.NavigateBackFromEditor();
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        var v = new FieldValidator();

        // Re-read the current state so we know what to keep for unchanged
        // (empty-textbox) fields. Any field left blank means "same as what's
        // in memory right now".
        ItemUser current;
        string curItemName, curDescription, curForgerName;
        try
        {
            // Identity gate BEFORE any write — the string writes below
            // (WriteStringWithFallback) hit game memory before the final
            // struct write, so this has to come first.
            if (!VerifyStillSameItem(out ItemNative liveNow)) return;
            _lastNative = liveNow;
            current = Base.ItemToUser(_lastNative);
            curItemName = Base.ReadUni<ItemNative>(Address, "EquipmentName") ?? "";
            curDescription = Base.ReadUni<ItemNative>(Address, "Description") ?? "";
            curForgerName = Base.ReadUni<ItemNative>(Address, "ForgerName") ?? "";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Read before write failed: " + ex.Message;
            return;
        }

        ItemUser user = current;

        // Hero Stats
        user.HeroHealth  = IntOr(v, TxtHeroHealth,  "Hero Health",  current.HeroHealth);
        user.HeroSpeed   = IntOr(v, TxtHeroSpeed,   "Hero Speed",   current.HeroSpeed);
        user.HeroDamage  = IntOr(v, TxtHeroDamage,  "Hero Damage",  current.HeroDamage);
        user.HeroCasting = IntOr(v, TxtHeroCasting, "Hero Casting", current.HeroCasting);
        user.HeroSkill1  = IntOr(v, TxtHeroSkill1,  "Hero Skill 1", current.HeroSkill1);
        user.HeroSkill2  = IntOr(v, TxtHeroSkill2,  "Hero Skill 2", current.HeroSkill2);

        // Tower Stats
        user.TowerHealth = IntOr(v, TxtTowerHealth, "Tower Health", current.TowerHealth);
        user.TowerSpeed  = IntOr(v, TxtTowerSpeed,  "Tower Speed",  current.TowerSpeed);
        user.TowerDamage = IntOr(v, TxtTowerDamage, "Tower Damage", current.TowerDamage);
        user.TowerRange  = IntOr(v, TxtTowerRange,  "Tower Range",  current.TowerRange);

        // Weapon
        user.Damage              = IntOr(v, TxtDamage,              "Weapon Damage",    current.Damage);
        user.RangedDamage        = IntOr(v, TxtRangedDamage,        "Ranged Damage",    current.RangedDamage);
        user.Blocking            = IntOr(v, TxtBlocking,            "Blocking",         current.Blocking);
        user.Knockback           = IntOr(v, TxtKnockback,           "Knockback",        current.Knockback);
        user.ChargeSpeed         = IntOr(v, TxtChargeSpeed,         "Charge Speed",     current.ChargeSpeed);
        user.ShotsPerSecond      = IntOr(v, TxtShotsPerSecond,      "Shots/Second",     current.ShotsPerSecond);
        user.NumberOfProjectiles = IntOr(v, TxtNumProjectiles,      "# Projectiles",    current.NumberOfProjectiles);
        user.SpeedOfProjectiles  = IntOr(v, TxtSpeedOfProjectiles,  "Projectile Speed", current.SpeedOfProjectiles);
        user.ClipAmmo            = IntOr(v, TxtClipAmmo,            "Clip Ammo",        current.ClipAmmo);
        user.ReloadSpeed         = IntOr(v, TxtReloadSpeed,         "Reload Speed",     current.ReloadSpeed);
        user.DrawScale           = FloatOr(v, TxtDrawScale,         "Draw Scale",       current.DrawScale);
        user.SwingSpeed          = FloatOr(v, TxtSwingSpeed,        "Swing Speed",      current.SwingSpeed);

        // Resistance — each is a DamageUser composed of value + (preserved) type
        int curGen = current.Generic?.Value ?? 0;
        int curPoi = current.Poison?.Value ?? 0;
        int curFir = current.Fire?.Value ?? 0;
        int curLig = current.Lightning?.Value ?? 0;
        user.Generic   = new DamageUser(true, new DamageNative { Value = IntOr(v, TxtGeneric,   "Generic Resist",   curGen), DamageType = _lastNative.DamageReductions[0].DamageType });
        user.Poison    = new DamageUser(true, new DamageNative { Value = IntOr(v, TxtPoison,    "Poison Resist",    curPoi), DamageType = _lastNative.DamageReductions[1].DamageType });
        user.Fire      = new DamageUser(true, new DamageNative { Value = IntOr(v, TxtFire,      "Fire Resist",      curFir), DamageType = _lastNative.DamageReductions[2].DamageType });
        user.Lightning = new DamageUser(true, new DamageNative { Value = IntOr(v, TxtLightning, "Lightning Resist", curLig), DamageType = _lastNative.DamageReductions[3].DamageType });

        // Elemental Damage
        int curEdv = current.ElementalDamage?.Value ?? 0;
        user.ElementalDamage = new DamageUser(false, new DamageNative { Value = IntOr(v, TxtElementalDamage, "Elemental Damage", curEdv), DamageType = _lastNative.WeaponAdditionalDamage.DamageType });

        // Quality
        user.Quality1 = ByteOr(v, TxtQuality1, "Quality 1", current.Quality1);
        if (CboQuality2.SelectedItem is Quality2Choice q2) user.Quality2 = q2.Value;
        if (CboQuality3.SelectedItem is Quality3 q3) user.Quality3 = q3;
        user.QualityFlag = ByteOr(v, TxtQualityFlag, "Quality Flag", current.QualityFlag);

        // Colors — the R/G/B boxes are kept populated so the picker/preview
        // works. DD1 items accept negative and HDR (>255) values for glow
        // effects, so parse as Int rather than Byte.
        // Colour overrides are written ONLY when the boxes differ from what
        // Refresh seeded. They used to be rebuilt from the boxes on every
        // UPDATE with alpha 0 — the layout is (A, R, G, B) — which turned an
        // item black the moment anything else on it was edited. When they
        // are written, A = 255 marks the override active, as the original
        // tool did.
        LinearColor c1 = new LinearColor();
        c1.R = v.Int(TxtColor1R, "Color 1 R");
        c1.G = v.Int(TxtColor1G, "Color 1 G");
        c1.B = v.Int(TxtColor1B, "Color 1 B");
        c1.A = 255;
        bool color1Edited = (c1.R, c1.G, c1.B) != _seedC1;
        user.Color1Override = c1;

        LinearColor c2 = new LinearColor();
        c2.R = v.Int(TxtColor2R, "Color 2 R");
        c2.G = v.Int(TxtColor2G, "Color 2 G");
        c2.B = v.Int(TxtColor2B, "Color 2 B");
        c2.A = 255;
        bool color2Edited = (c2.R, c2.G, c2.B) != _seedC2;
        user.Color2Override = c2;

        // Identity
        user.ItemName          = StrOr(TxtItemName, curItemName);
        user.Description       = StrOr(TxtDescription, curDescription);
        user.ForgerName        = StrOr(TxtForgerName, curForgerName);
        if (CboEquipmentType.SelectedItem is EquipmentType et) user.EquipmentType = et;
        user.EquipmentTemplate = StrOr(TxtEquipmentTemplate, current.EquipmentTemplate);

        // Economy
        user.MaximumValue = IntOr(v, TxtMaxValue, "Maximum Value", current.MaximumValue);
        user.MinimumValue = IntOr(v, TxtMinValue, "Minimum Value", current.MinimumValue);
        user.Rating       = FloatOr(v, TxtRating, "Rating", current.Rating);
        user.RatingPercent = FloatOr(v, TxtRatingPercent, "Rating %", current.RatingPercent);

        // Level
        user.Level            = IntOr(v, TxtLevel,            "Level",             current.Level);
        user.MaxLevel         = IntOr(v, TxtMaxLevel,         "Max Level",         current.MaxLevel);
        user.StoredMana       = IntOr(v, TxtStoredMana,       "Stored Mana",       current.StoredMana);
        user.LevelRequirement = ByteOr(v, TxtLevelRequirement, "Level Requirement", current.LevelRequirement);
        user.ID1              = IntOr(v, TxtID1,              "ID 1",              current.ID1);
        user.ID2              = IntOr(v, TxtID2,              "ID 2",              current.ID2);

        if (!v.IsValid)
        {
            StatusText.Text = $"Invalid input ({v.ErrorCount}): " + v.Report();
            v.FocusFirstError();
            return;
        }

        try
        {
            // Convert to native, preserving padding/pointers from last read
            ItemNative native = Base.ItemToNative(user);
            native._InstancePad = _lastNative._InstancePad;
            native.R0 = _lastNative.R0;
            native.R1 = _lastNative.R1;
            native.R2 = _lastNative.R2;
            native.R4 = _lastNative.R4;
            native.Flags = _lastNative.Flags;
            native.AdditionalAllowedUpgradeResistancePoints = _lastNative.AdditionalAllowedUpgradeResistancePoints;
            native.RequirementLevelOverride = _lastNative.RequirementLevelOverride;
            native.PrimaryColorSet = _lastNative.PrimaryColorSet;
            native.SecondaryColorSet = _lastNative.SecondaryColorSet;
            native.PrimaryColorSets = _lastNative.PrimaryColorSets;
            native.SecondaryColorSets = _lastNative.SecondaryColorSets;
            // Untouched colour boxes keep the item's own override bytes verbatim.
            if (!color1Edited) native.PrimaryColorOverride   = _lastNative.PrimaryColorOverride;
            if (!color2Edited) native.SecondaryColorOverride = _lastNative.SecondaryColorOverride;
            native.ShopMinimumSellWorth = _lastNative.ShopMinimumSellWorth;
            native.MaxRandomElementalDamageMultiplier = _lastNative.MaxRandomElementalDamageMultiplier;
            native.FolderID = _lastNative.FolderID;
            native.UserID = _lastNative.UserID;
            native.DroppedLocation = _lastNative.DroppedLocation;
            native.BaseEquipmentName = _lastNative.BaseEquipmentName;

            // Write strings. Try in-place first (fastest, safest for game
            // memory manager); if the new string doesn't fit, fall back to
            // WriteUni which allocates a new buffer. Without the fallback,
            // typing a name longer than the original silently no-ops.
            native.EquipmentName = WriteStringWithFallback(_lastNative.EquipmentName, user.ItemName ?? "", "EquipmentName");
            // Watermark.Apply appends "[Grandeu Reforged]" once, and
            // returns the text unchanged if it's already there — so an item
            // only ever grows its Description buffer on the first edit.
            native.Description   = WriteStringWithFallback(_lastNative.Description,   Watermark.Apply(ColorMarkup.NormalizeNewlines(user.Description)), "Description");
            native.ForgerName    = WriteStringWithFallback(_lastNative.ForgerName,    user.ForgerName ?? "",  "ForgerName");

            byte[] bytes = Base.Push(native);
            Base.Instance.WriteMemory(Address, bytes);

            StatusText.Text = $"{Base.Truncate(ItemDisplayName)} — updated";
            // Reload so placeholders reflect the new state and the boxes clear
            // — lets the user immediately edit again without re-selecting.
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Write error: " + ex.Message;
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Delete this item from game memory?\n\nThis zeros out the item data. Save a backup first.",
            "Delete Item", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            if (!VerifyStillSameItem(out _)) return;
            int size = Marshal.SizeOf(typeof(ItemNative));
            byte[] zeros = new byte[size];
            Base.Instance.WriteMemory(Address, zeros);
            StatusText.Text = "Deleted (zeroed)";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Delete error: " + ex.Message;
        }
    }

    // Color preview updates
    private void Color1_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateColorPreview(TxtColor1R, TxtColor1G, TxtColor1B, Color1Preview);
    }

    private void Color2_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateColorPreview(TxtColor2R, TxtColor2G, TxtColor2B, Color2Preview);
    }

    private void UpdateColorPreview(TextBox rBox, TextBox gBox, TextBox bBox, Border preview)
    {
        if (preview == null || rBox == null || gBox == null || bBox == null) return;
        // Tolerant parsing — preview updates on every keystroke, including
        // in-progress values. Full validation happens on Update.
        int.TryParse(rBox.Text, out int r);
        int.TryParse(gBox.Text, out int g);
        int.TryParse(bBox.Text, out int b);
        r = Math.Clamp(r, 0, 255);
        g = Math.Clamp(g, 0, 255);
        b = Math.Clamp(b, 0, 255);
        preview.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb((byte)r, (byte)g, (byte)b));
    }

    // Color picker dialogs
    private void BtnPickColor1_Click(object sender, RoutedEventArgs e)
    {
        var initial = new LinearColor();
        int.TryParse(TxtColor1R.Text, out int r); initial.R = r;
        int.TryParse(TxtColor1G.Text, out int g); initial.G = g;
        int.TryParse(TxtColor1B.Text, out int b); initial.B = b;

        var dlg = new ColorPickerDialog(initial);
        dlg.Owner = Window.GetWindow(this);
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            TxtColor1R.Text = dlg.Result.R.ToString();
            TxtColor1G.Text = dlg.Result.G.ToString();
            TxtColor1B.Text = dlg.Result.B.ToString();
        }
    }

    private void BtnPickColor2_Click(object sender, RoutedEventArgs e)
    {
        var initial = new LinearColor();
        int.TryParse(TxtColor2R.Text, out int r); initial.R = r;
        int.TryParse(TxtColor2G.Text, out int g); initial.G = g;
        int.TryParse(TxtColor2B.Text, out int b); initial.B = b;

        var dlg = new ColorPickerDialog(initial);
        dlg.Owner = Window.GetWindow(this);
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            TxtColor2R.Text = dlg.Result.R.ToString();
            TxtColor2G.Text = dlg.Result.G.ToString();
            TxtColor2B.Text = dlg.Result.B.ToString();
        }
    }

    // ── MAX stats ───────────────────────────────────────────────────
    //
    // Gear: open the config dialog to set per-field max values.
    // MAX: load the current in-memory values and apply the rules:
    //   - Hero/Tower stats + Description + Forger: always applied (if config has them)
    //   - Other numeric fields: applied only if the item's current value is non-zero
    //   - Config values left null: skipped entirely regardless of item state
    // Form fields get populated; user still has to click UPDATE to commit.

    private void BtnMaxConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new MaxItemConfigDialog(MaxItemConfig.Load());
        dlg.Owner = Window.GetWindow(this);
        dlg.ShowDialog();
    }

    private void BtnCopyTemplate_Click(object sender, RoutedEventArgs e)
    {
        // The box uses the placeholder pattern -- the "current" value lives
        // in Placeholder.Text while Text stays empty until the user edits.
        // Prefer what they typed; fall back to the placeholder otherwise.
        string text = TxtEquipmentTemplate.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
            text = Modinator.Behaviors.Placeholder.GetText(TxtEquipmentTemplate) ?? "";
        if (string.IsNullOrWhiteSpace(text)) return;
        try { Clipboard.SetText(text); }
        catch { /* clipboard can transiently fail when another app holds it */ }
    }

    private void BtnCopyMemoryAddress_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TxtMemoryAddress.Text); }
        catch { /* clipboard can transiently fail when another app holds it */ }
    }

    private void BtnMax_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = MaxItemConfig.Load();

            // Read current item state so we know which fields are non-zero.
            // MAX only fills the form (UPDATE does the write), but populating
            // it from a DIFFERENT item than the one on screen would mislead.
            if (!VerifyStillSameItem(out ItemNative liveNow)) return;
            _lastNative = liveNow;
            ItemUser cur = Base.ItemToUser(_lastNative);

            // Always-apply: hero/tower stats + strings.
            Set(TxtHeroHealth,  cfg.HeroHealth);
            Set(TxtHeroSpeed,   cfg.HeroSpeed);
            Set(TxtHeroDamage,  cfg.HeroDamage);
            Set(TxtHeroCasting, cfg.HeroCasting);
            Set(TxtHeroSkill1,  cfg.HeroSkill1);
            Set(TxtHeroSkill2,  cfg.HeroSkill2);
            Set(TxtTowerHealth, cfg.TowerHealth);
            Set(TxtTowerSpeed,  cfg.TowerSpeed);
            Set(TxtTowerDamage, cfg.TowerDamage);
            Set(TxtTowerRange,  cfg.TowerRange);

            // ── Weapon stats — class-aware via shared MaxCompat (same rules
            // the bulk path uses; see max_item_weapontype_research.md).
            // weaponType lives outside the marshaled ItemNative
            // (object+0x824 = Address + MaxCompat.WeaponTypeOffset).
            bool isWeapon = cur.EquipmentType == EquipmentType.Weapon;
            byte wt = 0;
            if (isWeapon)
            {
                try
                {
                    byte[]? b = Base.Instance.ReadMemory(Address + MaxCompat.WeaponTypeOffset, 1);
                    if (b != null && b.Length > 0) wt = b[0];
                }
                catch { }
            }
            bool WOk(WeaponStat s, bool present)
                => MaxCompat.WeaponStatApplies(s, cur.EquipmentType, wt, present);

            ApplyW (TxtDamage,             cfg.Damage,          WOk(WeaponStat.Damage,          cur.Damage != 0));
            ApplyW (TxtRangedDamage,       cfg.RangedDamage,    WOk(WeaponStat.RangedDamage,    cur.RangedDamage != 0));
            ApplyW (TxtBlocking,           cfg.Blocking,        WOk(WeaponStat.Blocking,        cur.Blocking != 0));
            ApplyW (TxtKnockback,          cfg.Knockback,       WOk(WeaponStat.Knockback,       cur.Knockback != 0));
            ApplyW (TxtChargeSpeed,        cfg.ChargeSpeed,     WOk(WeaponStat.ChargeSpeed,     cur.ChargeSpeed != 0));
            ApplyW (TxtShotsPerSecond,     cfg.ShotsPerSecond,  WOk(WeaponStat.ShotsPerSecond,  cur.ShotsPerSecond != 0));
            ApplyW (TxtNumProjectiles,     cfg.NumProjectiles,  WOk(WeaponStat.NumProjectiles,  cur.NumberOfProjectiles != 0));
            ApplyW (TxtSpeedOfProjectiles, cfg.ProjectileSpeed, WOk(WeaponStat.ProjectileSpeed, cur.SpeedOfProjectiles != 0));
            ApplyW (TxtClipAmmo,           cfg.ClipAmmo,        WOk(WeaponStat.ClipAmmo,        cur.ClipAmmo != 0));
            ApplyW (TxtReloadSpeed,        cfg.ReloadSpeed,     WOk(WeaponStat.ReloadSpeed,     cur.ReloadSpeed != 0));
            ApplyWF(TxtDrawScale,          cfg.DrawScale,       WOk(WeaponStat.DrawScale,       cur.DrawScale != 0f));
            // SwingSpeed: MaxCompat already excludes it for pets (Familiar).
            ApplyWF(TxtSwingSpeed,         cfg.SwingSpeed,      WOk(WeaponStat.SwingSpeed,      cur.SwingSpeed != 0f));

            ApplyW(TxtGeneric,   cfg.Generic,   MaxCompat.ResistApplies(cur.EquipmentType, (cur.Generic?.Value   ?? 0) != 0));
            ApplyW(TxtPoison,    cfg.Poison,    MaxCompat.ResistApplies(cur.EquipmentType, (cur.Poison?.Value    ?? 0) != 0));
            ApplyW(TxtFire,      cfg.Fire,      MaxCompat.ResistApplies(cur.EquipmentType, (cur.Fire?.Value      ?? 0) != 0));
            ApplyW(TxtLightning, cfg.Lightning, MaxCompat.ResistApplies(cur.EquipmentType, (cur.Lightning?.Value ?? 0) != 0));
            ApplyW(TxtElementalDamage, cfg.ElementalDamage,
                   MaxCompat.ElementalApplies(cur.EquipmentType, (cur.ElementalDamage?.Value ?? 0) != 0));

            SetIfNonzero(TxtQuality1,    cfg.Quality1,    cur.Quality1);
            SetIfNonzero(TxtQualityFlag, cfg.QualityFlag, cur.QualityFlag);

            SetIfNonzero(TxtLevel,            cfg.Level,            cur.Level);
            SetIfNonzero(TxtMaxLevel,         cfg.MaxLevel,         cur.MaxLevel);
            SetIfNonzero(TxtStoredMana,       cfg.StoredMana,       cur.StoredMana);
            // LevelRequirement populates unconditionally — every item has
            // one and users expect MAX to set it regardless of current val.
            Set(TxtLevelRequirement, cfg.LevelRequirement);
            SetIfNonzero(TxtID1,              cfg.ID1,              cur.ID1);
            SetIfNonzero(TxtID2,              cfg.ID2,              cur.ID2);

            SetIfNonzero(TxtMaxValue,        cfg.MaxValue,      cur.MaximumValue);
            SetIfNonzero(TxtMinValue,        cfg.MinValue,      cur.MinimumValue);
            SetIfNonzeroFloat(TxtRating,        cfg.Rating,        cur.Rating);
            SetIfNonzeroFloat(TxtRatingPercent, cfg.RatingPercent, cur.RatingPercent);

            // Strings — applied if config has them (non-empty).
            if (!string.IsNullOrEmpty(cfg.Description)) TxtDescription.Text = cfg.Description;
            if (!string.IsNullOrEmpty(cfg.ForgerName))  TxtForgerName.Text  = cfg.ForgerName;

            StatusText.Text = "MAX applied — click UPDATE to save";
        }
        catch (Exception ex)
        {
            StatusText.Text = "MAX failed: " + ex.Message;
        }
    }

    private static void Set(TextBox tb, int? v)
    {
        if (v.HasValue) tb.Text = v.Value.ToString();
    }

    private static void SetIfNonzero(TextBox tb, int? cfgMax, int current)
    {
        if (cfgMax.HasValue && current != 0) tb.Text = cfgMax.Value.ToString();
    }

    private static void SetIfNonzeroFloat(TextBox tb, float? cfgMax, float current)
    {
        if (cfgMax.HasValue && current != 0f)
            tb.Text = cfgMax.Value.ToString("G", CultureInfo.InvariantCulture);
    }

    private static void ApplyW(TextBox tb, int? cfgMax, bool ok)
    {
        if (ok && cfgMax.HasValue) tb.Text = cfgMax.Value.ToString();
    }

    private static void ApplyWF(TextBox tb, float? cfgMax, bool ok)
    {
        if (ok && cfgMax.HasValue)
            tb.Text = cfgMax.Value.ToString("G", CultureInfo.InvariantCulture);
    }
}
