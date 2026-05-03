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
}
