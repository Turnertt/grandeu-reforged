using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Modinator.Views;

public partial class WelcomeView : UserControl
{
    // Latest DD1 Steam build this app was verified working on. DD1 patches
    // ~weekly; bump this single string after the user confirms a clean
    // in-game pass (scans + calibrate + toggles) on the new build.
    private const string CompatibleDdVersion = "10.6.23";

    private DispatcherTimer? _statusTimer;

    public WelcomeView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Show 3-part Major.Minor.Build — the 4-part style (.Revision) was
        // auto-zero and added visual noise.
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v != null) VersionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}";

        CompatText.Text = $"Working as of DD1 v{CompatibleDdVersion}";

        UpdateConnectionStatus();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _statusTimer.Tick += (_, _) => UpdateConnectionStatus();
        _statusTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _statusTimer?.Stop();
        _statusTimer = null;
    }

    private void UpdateConnectionStatus()
    {
        bool running = false;
        try { running = Process.GetProcessesByName("DunDefGame").Length > 0; }
        catch { }

        if (running)
        {
            StatusDot.Fill = (Brush)FindResource("SuccessBrush");
            StatusLabel.Text = "Connected · DunDefGame.exe";
        }
        else
        {
            StatusDot.Fill = (Brush)FindResource("DangerBrush");
            StatusLabel.Text = "DunDefGame.exe not running";
        }
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string view = btn.Tag?.ToString() ?? "";
        if (Window.GetWindow(this) is MainWindow main)
            main.NavigateToView(view);
    }

    // Opens an external URL (GitHub) in the default browser. Best-effort:
    // a missing/blocked browser must never crash the home view (matches
    // the empty-catch convention used for other shell-out actions).
    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string url = btn.Tag?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { }
    }
}
