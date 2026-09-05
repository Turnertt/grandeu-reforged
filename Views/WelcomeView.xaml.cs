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
    private const string CompatibleDdVersion = "10.8.6";

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

    // Cached across ticks: a process can't change bitness without restarting,
    // and a restart flips `running`, so the extra check only costs anything on
    // the transition rather than every 1.5 s.
    private bool? _lastRunning;
    private bool? _is32Bit;

    private void UpdateConnectionStatus()
    {
        bool running = false;
        try { running = Process.GetProcessesByName("DunDefGame").Length > 0; }
        catch { }

        if (running != _lastRunning)
        {
            _lastRunning = running;
            _is32Bit = null;
            if (running)
            {
                try { _is32Bit = GameChain.GameIs32Bit(); } catch { }
            }
        }

        // A 64-bit game used to read as "Connected" here while nothing in the
        // tool actually worked — the worst possible message, since it points
        // the user away from the real cause.
        if (running && _is32Bit == false)
        {
            StatusDot.Fill = (Brush)FindResource("DangerBrush");
            StatusLabel.Text = "DunDefGame.exe is 64-bit · not supported";
        }
        else if (running)
        {
            StatusDot.Fill = (Brush)FindResource("SuccessBrush");
            StatusLabel.Text = "Connected · DunDefGame.exe";
        }
        else
        {
            StatusDot.Fill = (Brush)FindResource("DangerBrush");
            StatusLabel.Text = "DunDefGame.exe not running · 32-bit game only";
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
