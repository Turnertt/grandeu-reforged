using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Modinator.Views;

public partial class MaxHeroConfigDialog : Window
{
    public MaxHeroConfig Config { get; private set; }

    public MaxHeroConfigDialog(MaxHeroConfig current)
    {
        InitializeComponent();
        Config = current;
        LoadIntoForm(current);
    }

    // Populate TxtXxx.Text for non-null config fields; leave blank (= skip).
    private void LoadIntoForm(MaxHeroConfig c)
    {
        TxtHeroHealth.Text = c.HeroHealth?.ToString() ?? "";
        TxtHeroSpeed.Text = c.HeroSpeed?.ToString() ?? "";
        TxtHeroDamage.Text = c.HeroDamage?.ToString() ?? "";
        TxtHeroCasting.Text = c.HeroCasting?.ToString() ?? "";
        TxtHeroSkill1.Text = c.HeroSkill1?.ToString() ?? "";
        TxtHeroSkill2.Text = c.HeroSkill2?.ToString() ?? "";
        TxtTowerHealth.Text = c.TowerHealth?.ToString() ?? "";
        TxtTowerSpeed.Text = c.TowerSpeed?.ToString() ?? "";
        TxtTowerDamage.Text = c.TowerDamage?.ToString() ?? "";
        TxtTowerRange.Text = c.TowerRange?.ToString() ?? "";
        TxtLevel.Text = c.Level?.ToString() ?? "";
        TxtExperience.Text = c.Experience?.ToString() ?? "";
        TxtHeroName.Text = c.HeroName ?? "";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var c = new MaxHeroConfig
        {
            HeroHealth = ParseIntOrNull(TxtHeroHealth),
            HeroSpeed = ParseIntOrNull(TxtHeroSpeed),
            HeroDamage = ParseIntOrNull(TxtHeroDamage),
            HeroCasting = ParseIntOrNull(TxtHeroCasting),
            HeroSkill1 = ParseIntOrNull(TxtHeroSkill1),
            HeroSkill2 = ParseIntOrNull(TxtHeroSkill2),
            TowerHealth = ParseIntOrNull(TxtTowerHealth),
            TowerSpeed = ParseIntOrNull(TxtTowerSpeed),
            TowerDamage = ParseIntOrNull(TxtTowerDamage),
            TowerRange = ParseIntOrNull(TxtTowerRange),
            Level = ParseIntOrNull(TxtLevel),
            Experience = ParseIntOrNull(TxtExperience),
            HeroName = string.IsNullOrWhiteSpace(TxtHeroName.Text) ? null : TxtHeroName.Text,
        };
        c.Save();
        Config = c;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static int? ParseIntOrNull(TextBox tb)
        => int.TryParse((tb.Text ?? "").Trim(),
                        NumberStyles.Integer | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out int v) ? v : (int?)null;
}
