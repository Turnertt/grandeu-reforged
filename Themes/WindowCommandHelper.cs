using System.Windows;
using System.Windows.Input;

namespace Modinator.Themes;

/// <summary>
/// Attached behavior that automatically wires up SystemCommands
/// (Minimize, Maximize, Restore, Close) on a Window when Enabled is set to True.
/// Used by the ModernWindowStyle in Window.xaml.
/// </summary>
public class WindowCommandHelper : Freezable
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(WindowCommandHelper),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Window window && (bool)e.NewValue)
        {
            window.CommandBindings.Add(new CommandBinding(
                SystemCommands.CloseWindowCommand,
                (s, args) => SystemCommands.CloseWindow(window)));

            window.CommandBindings.Add(new CommandBinding(
                SystemCommands.MaximizeWindowCommand,
                (s, args) => SystemCommands.MaximizeWindow(window)));

            window.CommandBindings.Add(new CommandBinding(
                SystemCommands.MinimizeWindowCommand,
                (s, args) => SystemCommands.MinimizeWindow(window)));

            window.CommandBindings.Add(new CommandBinding(
                SystemCommands.RestoreWindowCommand,
                (s, args) => SystemCommands.RestoreWindow(window)));
        }
    }

    protected override Freezable CreateInstanceCore() => new WindowCommandHelper();
}
