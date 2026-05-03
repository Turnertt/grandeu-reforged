using System.Windows;

namespace Modinator.Behaviors;

// Attached property for grey hint text inside an empty TextBox. The visual is
// rendered by the TextBox template (see Themes/Controls.xaml) which overlays a
// TextBlock bound to Placeholder.Text and toggles its visibility via a Trigger
// on TextBox.Text being empty.
//
// Usage:
//   <TextBox beh:Placeholder.Text="current value" Text="" />
//
// Setting Text to non-empty hides the placeholder automatically. Intentional
// pattern: the caller leaves Text blank and stuffs the "previous" value into
// the placeholder, so the user can click and type over without clearing first.
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(Placeholder),
            new PropertyMetadata(""));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string v) => d.SetValue(TextProperty, v);
}
