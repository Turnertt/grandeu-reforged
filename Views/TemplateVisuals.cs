using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Modinator.Views;

// Same game stat artwork used by Item Edit, Forge cards and MAX settings.
internal static class TemplateVisuals
{
    private static readonly Dictionary<string, string> StatImages = new()
    {
        ["Hero health"] = "hero_health", ["Hero speed"] = "hero_speed", ["Hero damage"] = "hero_damage",
        ["Hero casting"] = "hero_casting", ["Tower health"] = "tower_health", ["Tower attack rate"] = "tower_speed",
        ["Tower damage"] = "tower_damage", ["Tower range"] = "tower_range", ["Damage"] = "weapon_damage",
        ["Ranged damage"] = "weapon_ranged", ["Generic resistance"] = "resist_generic",
        ["Poison resistance"] = "resist_poison", ["Fire resistance"] = "resist_fire", ["Lightning resistance"] = "resist_lightning",
        ["Blocking"] = "weapon_blocking", ["Knockback"] = "weapon_knockback", ["Charge speed"] = "weapon_chargespeed",
        ["Shots per second"] = "weapon_shotspersec", ["Projectiles"] = "weapon_projectiles",
        ["Projectile speed"] = "weapon_projspeed", ["Clip ammo"] = "weapon_clipammo", ["Reload speed"] = "weapon_reload",
        ["Stored mana"] = "mana_icon", ["Minimum sell value"] = "mana_icon", ["Maximum sell value"] = "mana_icon",
        ["Shop minimum value"] = "mana_icon"
    };

    public static string SectionGlyph(string name) => name switch
    {
        "Item bonuses" => "\uE734", "Combat" => "\uE945", "Upgrades" => "\uE74A",
        "Appearance" => "\uE790", "Item text" => "\uE8D2", _ => "\uE8F1"
    };

    public static FrameworkElement Icon(string label, double size = 22)
    {
        if (StatImages.TryGetValue(label, out string? file))
        {
            var image = new Image
            {
                Source = new BitmapImage(new Uri($"pack://application:,,,/GrandeuReforged;component/Assets/Icons/{file}.png")),
                Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }
        string glyph = label switch
        {
            "Ability 1" or "Ability 2" or "Elemental damage" or "Elemental multiplier" => "\uE945",
            "Size multiplier" => "\uE740", "Swing speed multiplier" => "\uE916",
            "Item name" or "Forger name" => "\uE70F", "Description" => "\uE8A5",
            "Rating" or "Rating percent" => "\uE734",
            _ when label.StartsWith("Primary ") || label.StartsWith("Secondary ") => "\uE790",
            _ => "\uE74A"
        };
        var icon = new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = size - 5,
            Width = size, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return icon;
    }

    public static FrameworkElement StatRow(string label, int value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Icon(label));
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var number = new TextBlock { Text = value.ToString("N0"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        number.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        Grid.SetColumn(number, 2);
        row.Children.Add(number);
        return row;
    }
}
