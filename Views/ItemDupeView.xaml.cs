using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Modinator.Views;

// Dedicated item-dupe tab. Two sides: SACRIFICIAL (left, red — gets
// overwritten) and SOURCE (right — the item to duplicate). Both pickers
// reuse the authoritative Forge enumeration via CloneSourcePickerDialog
// (ForgeViewerView.LastSnapshot).
//
// Transfer policy = the engine's own FEquipmentNetInfo value-set only
// (see project_dd1_offsets memory): start from the sacrificial (preserve
// EVERYTHING — identity, the 6 NativeArray buffers, Flags/Mystery/pad),
// copy ONLY value/archetype fields from source, write the 3 user strings
// in-place into the sacrificial's existing buffers. Never raw-copy the
// whole ItemNative; never copy a per-instance NativeArray pointer. This
// is the lowest-crash external dupe and supersedes the old (removed)
// ItemEditView "Clone From" button.
public partial class ItemDupeView : UserControl
{
    private int? _sacrificialAddr;
    private int? _sourceAddr;

    public ItemDupeView()
    {
        InitializeComponent();
    }

    private static bool EnsureScanned()
    {
        if (ForgeViewerView.LastSnapshot.Count > 0) return true;
        Base.RaiseMessage(
            "Open the Forge Viewer and SCAN first — the dupe pickers are built from the forge/hero item list.",
            "Item Dupe");
        return false;
    }

    private void BtnPickSacrificial_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureScanned()) return;
        var picker = new CloneSourcePickerDialog(
            excludeAddress: _sourceAddr ?? 0,
            titleOverride: "Pick SACRIFICIAL item (this item will be OVERWRITTEN)",
            promptOverride: "This item is destroyed — its stats/name become a copy of the source.",
            okButtonOverride: "USE AS SACRIFICIAL")
        { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() == true && picker.PickedAddress is int a)
        {
            _sacrificialAddr = a;
            ShowItem(true, a);
            RefreshDupeEnabled();
        }
    }

    private void BtnPickSource_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureScanned()) return;
        var picker = new CloneSourcePickerDialog(
            excludeAddress: _sacrificialAddr ?? 0,
            titleOverride: "Pick SOURCE item (the item to duplicate)",
            promptOverride: "The sacrificial item will become a copy of this one.",
            okButtonOverride: "USE AS SOURCE")
        { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() == true && picker.PickedAddress is int a)
        {
            _sourceAddr = a;
            ShowItem(false, a);
            RefreshDupeEnabled();
        }
    }

    private void RefreshDupeEnabled()
    {
        bool ready = _sacrificialAddr is int s && _sourceAddr is int src && s != src;
        BtnDupe.IsEnabled = ready;
        TxtStatus.Text = _sacrificialAddr == null || _sourceAddr == null
            ? "Pick a sacrificial and a source item."
            : (_sacrificialAddr == _sourceAddr
                ? "Sacrificial and source must be different items."
                : "Ready. DUPE overwrites the sacrificial.");
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        _sacrificialAddr = null;
        _sourceAddr = null;
        ClearCard(true);
        ClearCard(false);
        RefreshDupeEnabled();
    }

    private void ClearCard(bool sacrificial)
    {
        TextBlock nameT = sacrificial ? TxtSacName : TxtSrcName;
        TextBlock metaT = sacrificial ? TxtSacMeta : TxtSrcMeta;
        TextBlock addrT = sacrificial ? TxtSacAddr : TxtSrcAddr;
        nameT.Text = "— no item selected —";
        nameT.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextMutedBrush");
        metaT.Text = "";
        addrT.Text = "";
        (sacrificial ? SacStats : SrcStats).Children.Clear();
    }

    // Fills one card's three text blocks; flips the name from the muted
    // placeholder colour to the primary text colour once populated.
    private void ShowItem(bool sacrificial, int addr)
    {
        TextBlock nameT = sacrificial ? TxtSacName : TxtSrcName;
        TextBlock metaT = sacrificial ? TxtSacMeta : TxtSrcMeta;
        TextBlock addrT = sacrificial ? TxtSacAddr : TxtSrcAddr;
        try
        {
            int size = Marshal.SizeOf(typeof(ItemNative));
            var native = Base.Push<ItemNative>(Base.Instance.ReadMemory(addr, size));
            var u = Base.ItemToUser(native);
            string name = Base.ReadUni<ItemNative>(addr, "EquipmentName") ?? "";
            string forger = Base.ReadUni<ItemNative>(addr, "ForgerName") ?? "";
            if (string.IsNullOrWhiteSpace(name)) name = "(unnamed)";

            nameT.Text = name;
            nameT.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimaryBrush");
            metaT.Text = $"{u.EquipmentType}  •  {u.Quality2}  •  Lvl {u.Level}" +
                         (string.IsNullOrWhiteSpace(forger) ? "" : $"  •  forged by {forger}");
            addrT.Text = Base.AddressToString(addr);
            BuildStats(sacrificial ? SacStats : SrcStats, u);
        }
        catch (Exception ex)
        {
            nameT.Text = "(could not read item)";
            nameT.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");
            metaT.Text = ex.Message;
            addrT.Text = "";
            (sacrificial ? SacStats : SrcStats).Children.Clear();
        }
    }

    // Renders the item's stat block — same icon set / layout the Forge
    // Viewer card uses. Zero-value rows are dropped so a card only shows
    // what the item actually has.
    private void BuildStats(Panel host, ItemUser u)
    {
        host.Children.Clear();

        AddStatGrid(host, new (string?, string, int)[]
        {
            ("/Assets/Icons/hero_health.png",  "Hero HP",   u.HeroHealth),
            ("/Assets/Icons/hero_damage.png",  "Hero Dmg",  u.HeroDamage),
            ("/Assets/Icons/hero_speed.png",   "Hero Spd",  u.HeroSpeed),
            ("/Assets/Icons/hero_casting.png", "Casting",   u.HeroCasting),
            ("/Assets/Icons/tower_health.png", "Tower HP",  u.TowerHealth),
            ("/Assets/Icons/tower_damage.png", "Tower Dmg", u.TowerDamage),
            ("/Assets/Icons/tower_range.png",  "Tower Rng", u.TowerRange),
            ("/Assets/Icons/tower_speed.png",  "Tower Spd", u.TowerSpeed),
        });

        var wpn = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        AddChip(wpn, "/Assets/Icons/weapon_damage.png",      "Damage",      u.Damage);
        AddChip(wpn, "/Assets/Icons/weapon_ranged.png",      "Ranged",      u.RangedDamage);
        AddChip(wpn, "/Assets/Icons/weapon_knockback.png",   "Knockback",   u.Knockback);
        AddChip(wpn, "/Assets/Icons/weapon_projectiles.png", "Projectiles", u.NumberOfProjectiles);
        AddChip(wpn, "/Assets/Icons/weapon_projspeed.png",   "Proj Spd",    u.SpeedOfProjectiles);
        AddChip(wpn, "/Assets/Icons/weapon_reload.png",      "Reload",      u.ReloadSpeed);
        AddChip(wpn, "/Assets/Icons/weapon_chargespeed.png", "Charge",      u.ChargeSpeed);
        AddChip(wpn, "/Assets/Icons/weapon_shotspersec.png", "Shots/s",     u.ShotsPerSecond);
        AddChip(wpn, "/Assets/Icons/weapon_clipammo.png",    "Clip",        u.ClipAmmo);
        AddChip(wpn, "/Assets/Icons/weapon_blocking.png",    "Block",       u.Blocking);
        if (wpn.Children.Count > 0) { AddDivider(host); host.Children.Add(wpn); }

        var res = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        AddChip(res, "/Assets/Icons/resist_generic.png",   "Generic",   u.Generic?.Value   ?? 0);
        AddChip(res, "/Assets/Icons/resist_poison.png",    "Poison",    u.Poison?.Value    ?? 0);
        AddChip(res, "/Assets/Icons/resist_fire.png",      "Fire",      u.Fire?.Value      ?? 0);
        AddChip(res, "/Assets/Icons/resist_lightning.png", "Lightning", u.Lightning?.Value ?? 0);
        if (res.Children.Count > 0) { AddDivider(host); host.Children.Add(res); }
    }

    private void AddStatGrid(Panel host, (string? icon, string label, int value)[] stats)
    {
        var grid = new Grid();
        for (int c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        int shown = 0;
        foreach (var (icon, label, value) in stats)
        {
            if (value == 0) continue;
            int row = shown / 4, col = shown % 4;
            if (col == 0) grid.RowDefinitions.Add(new RowDefinition());
            AddIconStat(grid, row, col, icon, label, value);
            shown++;
        }
        if (shown > 0) host.Children.Add(grid);
    }

    private void AddDivider(Panel host) => host.Children.Add(new Border
    {
        Height = 1,
        Background = (Brush)FindResource("BorderBrush"),
        Opacity = 0.4,
        Margin = new Thickness(0, 8, 0, 0)
    });

    private void AddIconStat(Grid grid, int row, int col, string? iconPath, string label, int value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 3, 6, 3) };
        var valRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (iconPath != null)
            try
            {
                valRow.Children.Add(new Image
                {
                    Source = new BitmapImage(new Uri(iconPath, UriKind.Relative)),
                    Width = 14, Height = 14, Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85
                });
            }
            catch { }
        valRow.Children.Add(new TextBlock
        {
            Text = value.ToString("N0"),
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(valRow);
        panel.Children.Add(new TextBlock
        {
            Text = label, FontSize = 9.5,
            Foreground = (Brush)FindResource("TextMutedBrush")
        });
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, col);
        grid.Children.Add(panel);
    }

    private void AddChip(WrapPanel parent, string iconPath, string label, int value)
    {
        if (value == 0) return;
        var chip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 10, 4)
        };
        try
        {
            chip.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(iconPath, UriKind.Relative)),
                Width = 13, Height = 13, Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85
            });
        }
        catch { }
        chip.Children.Add(new TextBlock
        {
            Text = $"{label} {value:N0}",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        parent.Children.Add(chip);
    }

    private static string ShortName(int addr)
    {
        try
        {
            string name = Base.ReadUni<ItemNative>(addr, "EquipmentName") ?? "";
            return string.IsNullOrWhiteSpace(name) ? "(unnamed item)" : name;
        }
        catch { return "(unreadable item)"; }
    }

    private void BtnDupe_Click(object sender, RoutedEventArgs e)
    {
        if (_sacrificialAddr is not int sacAddr || _sourceAddr is not int srcAddr || sacAddr == srcAddr)
            return;

        var ok = MessageBox.Show(
            $"Overwrite \"{ShortName(sacAddr)}\" with a copy of \"{ShortName(srcAddr)}\"?\n\n" +
            "This permanently replaces the sacrificial item.",
            "Confirm Dupe", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes) return;

        try
        {
            int size = Marshal.SizeOf(typeof(ItemNative));
            ItemNative source = Base.Push<ItemNative>(Base.Instance.ReadMemory(srcAddr, size));
            ItemNative target = Base.Push<ItemNative>(Base.Instance.ReadMemory(sacAddr, size));

            // Start from the sacrificial so EVERYTHING is preserved by
            // default: identity (EquipmentID1/2, FolderID, UserID,
            // DroppedLocation), engine UObject state (Flags, Mystery,
            // _InstancePad, R0/R1/R2/R4), and all 6 NativeArray buffer
            // pointers. Then copy ONLY the FEquipmentNetInfo value-set
            // from source.
            ItemNative merged = target;

            merged.EquipmentTemplate                    = source.EquipmentTemplate;
            merged.StatModifiers                        = source.StatModifiers;
            merged.DamageReductions                     = source.DamageReductions;
            merged.WeaponDamageBonus                    = source.WeaponDamageBonus;
            merged.WeaponNumberOfProjectilesBonus       = source.WeaponNumberOfProjectilesBonus;
            merged.WeaponSpeedOfProjectilesBonus        = source.WeaponSpeedOfProjectilesBonus;
            merged.WeaponAdditionalDamage               = source.WeaponAdditionalDamage;
            merged.WeaponDrawScaleMultiplier            = source.WeaponDrawScaleMultiplier;
            merged.MaxRandomElementalDamageMultiplier   = source.MaxRandomElementalDamageMultiplier;
            merged.WeaponSwingSpeedMultiplier           = source.WeaponSwingSpeedMultiplier;
            merged.WeaponReloadSpeedBonus               = source.WeaponReloadSpeedBonus;
            merged.WeaponKnockbackBonus                 = source.WeaponKnockbackBonus;
            merged.WeaponAltDamageBonus                 = source.WeaponAltDamageBonus;
            merged.WeaponBlockingBonus                  = source.WeaponBlockingBonus;
            merged.WeaponClipAmmoBonus                  = source.WeaponClipAmmoBonus;
            merged.AdditionalAllowedUpgradeResistancePoints = source.AdditionalAllowedUpgradeResistancePoints;
            merged.RequirementLevelOverride             = source.RequirementLevelOverride;
            merged.WeaponChargeSpeedBonus               = source.WeaponChargeSpeedBonus;
            merged.WeaponShotsPerSecondBonus            = source.WeaponShotsPerSecondBonus;
            merged.NameIndex_Base                       = source.NameIndex_Base;
            merged.NameIndex_QualityDescriptor          = source.NameIndex_QualityDescriptor;
            merged.NameIndex_DamageReduction            = source.NameIndex_DamageReduction;
            merged.PrimaryColorSet                      = source.PrimaryColorSet;
            merged.SecondaryColorSet                    = source.SecondaryColorSet;
            merged.ManualLR                             = source.ManualLR;
            merged.EquipmentType                        = source.EquipmentType;
            merged.PrimaryColorOverride                 = source.PrimaryColorOverride;
            merged.SecondaryColorOverride               = source.SecondaryColorOverride;
            merged.MaximumSellWorth                     = source.MaximumSellWorth;
            merged.MinimumSellWorth                     = source.MinimumSellWorth;
            merged.ShopMinimumSellWorth                 = source.ShopMinimumSellWorth;
            merged.MaxEquipmentLevel                    = source.MaxEquipmentLevel;
            merged.Level                                = source.Level;
            merged.StoredMana                           = source.StoredMana;
            merged.MyRatingPercent                      = source.MyRatingPercent;
            merged.MyRating                             = source.MyRating;

            // The 3 user strings: write the SOURCE text into the
            // SACRIFICIAL's existing buffer (in-place, or fresh-alloc
            // inside DD1 if it doesn't fit). Never copy source's pointer.
            // Blank name crashes the game (documented) → use a single
            // space. BaseEquipmentName is left as the sacrificial's — its
            // displayed base name resolves from the copied EquipmentTemplate.
            string srcName = Base.ReadUni<ItemNative>(srcAddr, "EquipmentName") ?? "";
            string srcDesc = Base.ReadUni<ItemNative>(srcAddr, "Description") ?? "";
            string srcForg = Base.ReadUni<ItemNative>(srcAddr, "ForgerName") ?? "";
            if (string.IsNullOrEmpty(srcName)) srcName = " ";

            merged.EquipmentName = WriteStr(target.EquipmentName, srcName, sacAddr, "EquipmentName");
            merged.Description   = WriteStr(target.Description,   srcDesc, sacAddr, "Description");
            merged.ForgerName    = WriteStr(target.ForgerName,    srcForg, sacAddr, "ForgerName");

            Base.Instance.WriteMemory(sacAddr, Base.Push(merged));

            ShowItem(true, sacAddr);
            TxtStatus.Text = "Duped. Sacrificial overwritten from 0x" + srcAddr.ToString("X8") +
                             ". Re-scan the Forge Viewer to refresh lists.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Dupe failed: " + ex.Message;
        }
    }

    // Mirrors the (kept) ItemEditView string-write policy: overwrite in
    // place when the sacrificial's buffer is big enough, else fresh-alloc
    // a new buffer inside DD1 and point the field at it.
    private static NativeArray WriteStr(NativeArray existing, string data, int itemAddr, string field)
    {
        if (existing.MaximumLength >= data.Length + 1)
            return Base.WriteUniInPlace(existing, data);
        return Base.WriteUni(itemAddr, field, data);
    }
}
