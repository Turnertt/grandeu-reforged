using System.Windows;
using System.Windows.Controls;

namespace Modinator.Views;

public partial class HeroSearchView : UserControl
{
    public HeroSearchView()
    {
        InitializeComponent();
    }

    private HeroUserSearch BuildSearch()
    {
        var s = new HeroUserSearch();
        int.TryParse(TxtHeroHealth.Text, out int hh); s.HeroHealth = hh;
        int.TryParse(TxtHeroDamage.Text, out int hd); s.HeroDamage = hd;
        int.TryParse(TxtHeroSpeed.Text, out int hs); s.HeroSpeed = hs;
        int.TryParse(TxtHeroCasting.Text, out int hc); s.HeroCasting = hc;
        int.TryParse(TxtHeroSkill1.Text, out int sk1); s.HeroSkill1 = sk1;
        int.TryParse(TxtHeroSkill2.Text, out int sk2); s.HeroSkill2 = sk2;
        int.TryParse(TxtTowerHealth.Text, out int th); s.TowerHealth = th;
        int.TryParse(TxtTowerDamage.Text, out int td); s.TowerDamage = td;
        int.TryParse(TxtTowerRange.Text, out int tr); s.TowerRange = tr;
        int.TryParse(TxtTowerSpeed.Text, out int ts); s.TowerSpeed = ts;
        s.HeroName = TxtHeroName.Text ?? "";
        int.TryParse(TxtLevel.Text, out int lv); s.Level = lv;
        int.TryParse(TxtExperience.Text, out int xp); s.Experience = xp;
        return s;
    }

    private void BtnFirstScan_Click(object sender, RoutedEventArgs e)
    {
        if (!Base.OpenProcess()) return;

        ScanStatus.Text = "Scanning...";

        var search = BuildSearch();
        var native = Base.HeroToNative(search);
        Base.CreateHeroMask(native);
        Base.RunFirstScan(0, 4, OnFail, OnSuccess, ref Base.HeroResults);
    }

    private void BtnNextScan_Click(object sender, RoutedEventArgs e)
    {
        if (!Base.OpenProcess()) return;
        ScanStatus.Text = "Scanning...";

        var search = BuildSearch();
        var native = Base.HeroToNative(search);
        Base.CreateHeroMask(native);
        Base.RunNextScan(OnFail, OnSuccess, ref Base.HeroResults);
    }

    private void OnSuccess()
    {
        BtnNextScan.IsEnabled = true;
        ScanStatus.Text = $"Found {Base.HeroResults.Count:N0} results";
        Base.RaiseResultsChanged(Base.HeroResults);
    }

    private void OnFail()
    {
        BtnNextScan.IsEnabled = false;
        ScanStatus.Text = "No results";
        Base.HeroResults.Clear();
        Base.RaiseResultsChanged(Base.HeroResults);
    }

    private void BtnNewScan_Click(object sender, RoutedEventArgs e)
    {
        TxtHeroHealth.Text = ""; TxtHeroDamage.Text = ""; TxtHeroSpeed.Text = ""; TxtHeroCasting.Text = "";
        TxtHeroSkill1.Text = ""; TxtHeroSkill2.Text = "";
        TxtTowerHealth.Text = ""; TxtTowerDamage.Text = ""; TxtTowerRange.Text = ""; TxtTowerSpeed.Text = "";
        TxtHeroName.Text = "";
        TxtLevel.Text = ""; TxtExperience.Text = "";

        BtnNextScan.IsEnabled = false;
        Base.HeroResults.Clear();
        Base.RaiseResultsChanged(Base.HeroResults);
        ScanStatus.Text = "Enter search values and click First Scan";
    }
}
