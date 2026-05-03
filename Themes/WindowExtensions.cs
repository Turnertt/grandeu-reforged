using System.Windows;

namespace Modinator.Themes;

/// <summary>
/// Attached property for injecting content into the ModernWindowStyle title bar.
/// MainWindow uses this to host icon toggles next to the min/max/close buttons;
/// dialogs leave it unset and get an empty title bar as before.
/// </summary>
public static class WindowExtensions
{
    public static readonly DependencyProperty TitleBarContentProperty =
        DependencyProperty.RegisterAttached(
            "TitleBarContent",
            typeof(object),
            typeof(WindowExtensions),
            new PropertyMetadata(null));

    public static object? GetTitleBarContent(DependencyObject d) => d.GetValue(TitleBarContentProperty);
    public static void SetTitleBarContent(DependencyObject d, object? v) => d.SetValue(TitleBarContentProperty, v);
}
