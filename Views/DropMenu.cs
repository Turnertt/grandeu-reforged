using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Modinator.Views;

// A themed dropdown under a top-bar button ("MORE ⌄"): the less-used
// actions of a screen. Entries are icon + text rows; a danger entry is red.
internal sealed class DropMenu
{
    private readonly Popup _popup;
    private readonly StackPanel _entries = new();
    private DateTime _closedAt = DateTime.MinValue;

    // Raised just before the menu opens (enable / disable entries here).
    public event Action? Opening;

    public DropMenu(Button button)
    {
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 4, 0, 0),
            MinWidth = 190,
            Child = _entries
        };
        frame.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = frame
        };
        _popup.Closed += (_, _) => _closedAt = DateTime.UtcNow;
        button.Click += (_, _) =>
        {
            // The click that closes an open menu (StaysOpen=false) also lands
            // on the button; don't let it reopen the menu it just closed.
            if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < 250) return;
            Opening?.Invoke();
            _popup.IsOpen = true;
        };
    }

    // Button content for the button that owns a menu: the label, then a chevron.
    public static object ButtonContent(string text)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock
        {
            Text = "\uE70D",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 9,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        return content;
    }

    public FrameworkElement Add(string glyph, string text, Action onClick, bool danger = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Width = 22,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, danger ? "DangerBrush" : "TextSecondaryBrush");
        var label = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, danger ? "DangerBrush" : "TextPrimaryBrush");
        row.Children.Add(icon);
        row.Children.Add(label);
        var entry = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 7, 14, 7),
            Cursor = Cursors.Hand,
            Child = row
        };
        entry.MouseEnter += (_, _) => entry.SetResourceReference(Border.BackgroundProperty, "SurfaceLightBrush");
        entry.MouseLeave += (_, _) => entry.Background = Brushes.Transparent;
        entry.MouseLeftButtonUp += (_, _) =>
        {
            _popup.IsOpen = false;
            onClick();
        };
        _entries.Children.Add(entry);
        return entry;
    }

    public static void SetEnabled(FrameworkElement entry, bool enabled)
    {
        entry.IsEnabled = enabled;
        entry.Opacity = enabled ? 1 : 0.45;
    }
}
