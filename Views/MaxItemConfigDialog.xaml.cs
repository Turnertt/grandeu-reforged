using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Modinator.Views;

public partial class MaxItemConfigDialog : Window
{
    public MaxItemConfig Config { get; private set; }

    public MaxItemConfigDialog(MaxItemConfig current)
    {
        InitializeComponent();
        Config = current;
        LoadIntoForm(current);
    }

    private void LoadIntoForm(MaxItemConfig c)
    {
        TxtHeroHealth.Text       = NullOrString(c.HeroHealth);
        TxtHeroSpeed.Text        = NullOrString(c.HeroSpeed);
        TxtHeroDamage.Text       = NullOrString(c.HeroDamage);
        TxtHeroCasting.Text      = NullOrString(c.HeroCasting);
        TxtHeroSkill1.Text       = NullOrString(c.HeroSkill1);
        TxtHeroSkill2.Text       = NullOrString(c.HeroSkill2);
        TxtTowerHealth.Text      = NullOrString(c.TowerHealth);
        TxtTowerSpeed.Text       = NullOrString(c.TowerSpeed);
        TxtTowerDamage.Text      = NullOrString(c.TowerDamage);
        TxtTowerRange.Text       = NullOrString(c.TowerRange);
        TxtDamage.Text           = NullOrString(c.Damage);
        TxtRangedDamage.Text     = NullOrString(c.RangedDamage);
        TxtBlocking.Text         = NullOrString(c.Blocking);
        TxtKnockback.Text        = NullOrString(c.Knockback);
        TxtChargeSpeed.Text      = NullOrString(c.ChargeSpeed);
        TxtShotsPerSecond.Text   = NullOrString(c.ShotsPerSecond);
        TxtNumProjectiles.Text   = NullOrString(c.NumProjectiles);
        TxtProjectileSpeed.Text  = NullOrString(c.ProjectileSpeed);
        TxtClipAmmo.Text         = NullOrString(c.ClipAmmo);
        TxtReloadSpeed.Text      = NullOrString(c.ReloadSpeed);
        TxtDrawScale.Text        = c.DrawScale?.ToString(CultureInfo.InvariantCulture) ?? "";
        TxtSwingSpeed.Text       = c.SwingSpeed?.ToString(CultureInfo.InvariantCulture) ?? "";
        TxtGeneric.Text          = NullOrString(c.Generic);
        TxtPoison.Text           = NullOrString(c.Poison);
        TxtFire.Text             = NullOrString(c.Fire);
        TxtLightning.Text        = NullOrString(c.Lightning);
        TxtElementalDamage.Text  = NullOrString(c.ElementalDamage);
        TxtQuality1.Text         = NullOrString(c.Quality1);
        TxtQualityFlag.Text      = NullOrString(c.QualityFlag);
        TxtLevel.Text            = NullOrString(c.Level);
        TxtMaxLevel.Text         = NullOrString(c.MaxLevel);
        TxtStoredMana.Text       = NullOrString(c.StoredMana);
        TxtLevelRequirement.Text = NullOrString(c.LevelRequirement);
        TxtID1.Text              = NullOrString(c.ID1);
        TxtID2.Text              = NullOrString(c.ID2);
        TxtMaxValue.Text         = NullOrString(c.MaxValue);
        TxtMinValue.Text         = NullOrString(c.MinValue);
        TxtRating.Text           = c.Rating?.ToString(CultureInfo.InvariantCulture) ?? "";
        TxtRatingPercent.Text    = c.RatingPercent?.ToString(CultureInfo.InvariantCulture) ?? "";
        TxtDescription.Text      = c.Description ?? "";
        TxtForgerName.Text       = c.ForgerName ?? "";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var c = new MaxItemConfig
        {
            HeroHealth       = Int(TxtHeroHealth),
            HeroSpeed        = Int(TxtHeroSpeed),
            HeroDamage       = Int(TxtHeroDamage),
            HeroCasting      = Int(TxtHeroCasting),
            HeroSkill1       = Int(TxtHeroSkill1),
            HeroSkill2       = Int(TxtHeroSkill2),
            TowerHealth      = Int(TxtTowerHealth),
            TowerSpeed       = Int(TxtTowerSpeed),
            TowerDamage      = Int(TxtTowerDamage),
            TowerRange       = Int(TxtTowerRange),
            Damage           = Int(TxtDamage),
            RangedDamage     = Int(TxtRangedDamage),
            Blocking         = Int(TxtBlocking),
            Knockback        = Int(TxtKnockback),
            ChargeSpeed      = Int(TxtChargeSpeed),
            ShotsPerSecond   = Int(TxtShotsPerSecond),
            NumProjectiles   = Int(TxtNumProjectiles),
            ProjectileSpeed  = Int(TxtProjectileSpeed),
            ClipAmmo         = Int(TxtClipAmmo),
            ReloadSpeed      = Int(TxtReloadSpeed),
            DrawScale        = Float(TxtDrawScale),
            SwingSpeed       = Float(TxtSwingSpeed),
            Generic          = Int(TxtGeneric),
            Poison           = Int(TxtPoison),
            Fire             = Int(TxtFire),
            Lightning        = Int(TxtLightning),
            ElementalDamage  = Int(TxtElementalDamage),
            Quality1         = Int(TxtQuality1),
            QualityFlag      = Int(TxtQualityFlag),
            Level            = Int(TxtLevel),
            MaxLevel         = Int(TxtMaxLevel),
            StoredMana       = Int(TxtStoredMana),
            LevelRequirement = Int(TxtLevelRequirement),
            ID1              = Int(TxtID1),
            ID2              = Int(TxtID2),
            MaxValue         = Int(TxtMaxValue),
            MinValue         = Int(TxtMinValue),
            Rating           = Float(TxtRating),
            RatingPercent    = Float(TxtRatingPercent),
            Description      = string.IsNullOrWhiteSpace(TxtDescription.Text) ? null : TxtDescription.Text,
            ForgerName       = string.IsNullOrWhiteSpace(TxtForgerName.Text) ? null : TxtForgerName.Text,
        };
        c.Save();
        Config = c;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string NullOrString(int? v) => v.HasValue ? v.Value.ToString() : "";

    private static int? Int(TextBox tb)
        => int.TryParse((tb.Text ?? "").Trim(),
                        NumberStyles.Integer | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out int v) ? v : (int?)null;

    private static float? Float(TextBox tb)
        => float.TryParse((tb.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : (float?)null;
}
