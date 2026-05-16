using System;
using System.Windows;
using System.Windows.Controls;

namespace Modinator.Views;

public partial class ItemSearchView : UserControl
{
    public ItemSearchView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CboEquipmentType.Items.Clear();
        CboEquipmentType.Items.Add(EquipmentType.All);
        foreach (var val in Enum.GetValues(typeof(EquipmentType)))
        {
            if ((EquipmentType)val != EquipmentType.All)
                CboEquipmentType.Items.Add(val);
        }
        CboEquipmentType.SelectedIndex = 0;
    }

    private ItemSearch BuildSearch()
    {
        var s = new ItemSearch();
        int.TryParse(TxtHeroHealth.Text, out int hh); s.HeroHealth = hh;
        int.TryParse(TxtHeroSpeed.Text, out int hs); s.HeroSpeed = hs;
        int.TryParse(TxtHeroDamage.Text, out int hd); s.HeroDamage = hd;
        int.TryParse(TxtHeroCasting.Text, out int hc); s.HeroCasting = hc;
        int.TryParse(TxtHeroSkill1.Text, out int sk1); s.HeroSkill1 = sk1;
        int.TryParse(TxtHeroSkill2.Text, out int sk2); s.HeroSkill2 = sk2;
        int.TryParse(TxtTowerHealth.Text, out int th); s.TowerHealth = th;
        int.TryParse(TxtTowerSpeed.Text, out int ts); s.TowerSpeed = ts;
        int.TryParse(TxtTowerDamage.Text, out int td); s.TowerDamage = td;
        int.TryParse(TxtTowerRange.Text, out int tr); s.TowerRange = tr;
        int.TryParse(TxtGeneric.Text, out int g); s.Generic = g;
        int.TryParse(TxtPoison.Text, out int p); s.Poison = p;
        int.TryParse(TxtFire.Text, out int f); s.Fire = f;
        int.TryParse(TxtLightning.Text, out int l); s.Lightning = l;
        int.TryParse(TxtKnockback.Text, out int kb); s.Knockback = kb;
        int.TryParse(TxtChargeSpeed.Text, out int cs); s.ChargeSpeed = cs;
        int.TryParse(TxtNumProjectiles.Text, out int np); s.NumberOfProjectiles = np;
        int.TryParse(TxtProjectileSpeed.Text, out int ps); s.SpeedOfProjectiles = ps;
        int.TryParse(TxtReloadSpeed.Text, out int rs); s.ReloadSpeed = rs;
        s.Description = TxtDescription.Text ?? "";
        if (CboEquipmentType.SelectedItem is EquipmentType et) s.EquipmentType = et;
        int.TryParse(TxtLevel.Text, out int lv); s.Level = lv;
        int.TryParse(TxtMaxLevel.Text, out int ml); s.MaxLevel = ml;
        return s;
    }

    private void BtnFirstScan_Click(object sender, RoutedEventArgs e)
    {
        if (!Base.OpenProcess()) return;

        ScanStatus.Text = "Scanning...";

        var search = BuildSearch();
        var native = Base.ItemToNative(search);
        Base.CreateItemMask(native);
        Base.RunFirstScan(56, 256, OnFail, OnSuccess, ref Base.ItemResults);
    }

    private void BtnNextScan_Click(object sender, RoutedEventArgs e)
    {
        if (!Base.OpenProcess()) return;
        ScanStatus.Text = "Scanning...";

        var search = BuildSearch();
        var native = Base.ItemToNative(search);
        Base.CreateItemMask(native);
        Base.RunNextScan(OnFail, OnSuccess, ref Base.ItemResults);
    }

    private void OnSuccess()
    {
        BtnNextScan.IsEnabled = true;
        ScanStatus.Text = $"Found {Base.ItemResults.Count:N0} results";
        Base.RaiseResultsChanged(Base.ItemResults);
    }

    private void OnFail()
    {
        BtnNextScan.IsEnabled = false;
        ScanStatus.Text = "No results";
        Base.ItemResults.Clear();
        Base.RaiseResultsChanged(Base.ItemResults);
    }

    private void BtnNewScan_Click(object sender, RoutedEventArgs e)
    {
        // Clear every input back to empty (placeholder shows "0") and drop
        // the result list so the next FIRST SCAN starts clean.
        TxtHeroHealth.Text = ""; TxtHeroSpeed.Text = ""; TxtHeroDamage.Text = "";
        TxtHeroCasting.Text = ""; TxtHeroSkill1.Text = ""; TxtHeroSkill2.Text = "";
        TxtTowerHealth.Text = ""; TxtTowerSpeed.Text = ""; TxtTowerDamage.Text = "";
        TxtTowerRange.Text = "";
        TxtGeneric.Text = ""; TxtPoison.Text = ""; TxtFire.Text = ""; TxtLightning.Text = "";
        TxtKnockback.Text = ""; TxtChargeSpeed.Text = "";
        TxtNumProjectiles.Text = ""; TxtProjectileSpeed.Text = ""; TxtReloadSpeed.Text = "";
        TxtLevel.Text = ""; TxtMaxLevel.Text = "";
        TxtDescription.Text = "";
        if (CboEquipmentType.Items.Count > 0) CboEquipmentType.SelectedIndex = 0;

        BtnNextScan.IsEnabled = false;
        Base.ItemResults.Clear();
        Base.RaiseResultsChanged(Base.ItemResults);
        ScanStatus.Text = "Enter search values and click First Scan";
    }
}
