using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Modinator.Views;

internal enum ToastKind { Info, Success, Error }

// Non-blocking notices in the bottom-right corner of the active window (the
// ModernWindowStyle template carries the host panel, so dialogs get them
// too). They replace the OK-only message boxes: nothing to dismiss, and a
// repeating failure can't stack modal dialogs. Questions that need an
// answer (overwrite, delete, restore) stay as dialogs.
//
// Info / success fade after a few seconds, errors stay longer; a click
// dismisses one. The same text shown again restarts its timer instead of
// adding a second copy, and only a few are kept on screen.
internal static class Toast
{
    private const string HostName = "PART_ToastHost";
    private const int MaxVisible = 4;

    public static void Show(string message, ToastKind kind = ToastKind.Info, string? title = null)
    {
        var app = Application.Current;
        if (app == null || string.IsNullOrWhiteSpace(message)) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => Show(message, kind, title));
            return;
        }

        Panel? host = FindHost();
        if (host == null)
        {
            // No themed window up yet (startup): fall back to a plain box.
            MessageBox.Show(message, title ?? "Grandeu: Reforged", MessageBoxButton.OK);
            return;
        }

        string key = kind + "|" + title + "|" + message;
        foreach (var child in host.Children)
            if (child is Border existing && existing.Tag is ToastState state && state.Key == key)
            {
                state.Timer.Stop();
                state.Timer.Start();
                return;
            }
        while (host.Children.Count >= MaxVisible) host.Children.RemoveAt(0);

        host.Children.Add(Build(host, message, kind, title, key));
    }

    private sealed record ToastState(string Key, DispatcherTimer Timer);

    // The active window's host, else the main window's.
    private static Panel? FindHost()
    {
        var app = Application.Current;
        Window? active = null;
        foreach (Window w in app.Windows)
            if (w.IsActive && w.IsVisible) { active = w; break; }
        foreach (var window in new[] { active, app.MainWindow })
        {
            if (window == null || !window.IsLoaded) continue;
            if (window.Template?.FindName(HostName, window) is Panel panel) return panel;
        }
        return null;
    }

    private static Border Build(Panel host, string message, ToastKind kind, string? title, string key)
    {
        string brushKey = kind switch
        {
            ToastKind.Error => "DangerBrush",
            ToastKind.Success => "SuccessBrush",
            _ => "AccentBrush"
        };
        string glyph = kind switch
        {
            ToastKind.Error => "\uEA39",    // error badge
            ToastKind.Success => "\uE73E",  // check mark
            _ => "\uE946"                   // info
        };

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 15,
            Margin = new Thickness(0, 1, 10, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);

        var text = new StackPanel();
        if (!string.IsNullOrWhiteSpace(title))
        {
            var heading = new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            text.Children.Add(heading);
        }
        var body = new TextBlock { Text = message.Trim(), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, LineHeight = 16 };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        text.Children.Add(body);

        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        // Colored stripe down the left edge, inside the rounded border.
        var stripe = new Border { Width = 3, CornerRadius = new CornerRadius(6, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
        stripe.SetResourceReference(Border.BackgroundProperty, brushKey);
        var layout = new Grid();
        layout.Children.Add(stripe);
        layout.Children.Add(new Border { Padding = new Thickness(15, 10, 14, 11), Child = row });

        var toast = new Border
        {
            Width = 360,
            Margin = new Thickness(0, 8, 0, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Opacity = 0,
            Child = layout,
            ToolTip = "Click to dismiss",
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black }
        };
        toast.SetResourceReference(Border.BackgroundProperty, "SurfaceLightBrush");
        toast.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 9 : 4.5) };
        toast.Tag = new ToastState(key, timer);

        void Dismiss()
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => host.Children.Remove(toast);
            toast.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        timer.Tick += (_, _) => Dismiss();
        toast.MouseLeftButtonUp += (_, _) => Dismiss();
        // Reading it keeps it up.
        toast.MouseEnter += (_, _) => timer.Stop();
        toast.MouseLeave += (_, _) => timer.Start();

        toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        timer.Start();
        return toast;
    }
}
