using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Modinator.Views;

public partial class SettingsView : UserControl
{
    private bool _suppressHandlers;
    private Button? _capturingHkButton;  // the button currently waiting for a new combo

    public SettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SyncFromState();

    // Call whenever the underlying state changes (e.g. a hotkey toggled a
    // feature) so the switches stay in sync with reality. _suppressHandlers
    // blocks the Toggled events that setting IsChecked would otherwise fire.
    public void SyncFromState()
    {
        _suppressHandlers = true;
        SwFullScanning.IsChecked = Base.FullScan;
        SwPauseOnScan.IsChecked = Base.PauseScan;
        SwSimulateG.IsChecked = Base.SimulateG;
        if (Window.GetWindow(this) is MainWindow main)
        {
            SwAlwaysOnTop.IsChecked = main.Topmost;
            SwAutoKill.IsChecked = main.AutoKillEnabled;
            SwUnlimitedMana.IsChecked = main.UnlimitedManaEnabled;
            SwMaxTowerUnits.IsChecked = main.MaxTowerUnitsEnabled;
            RefreshHotkeyLabels(main);
        }
        TxtOverrideStatus.Text = Tunables.Status;
        TxtOverridePath.Text = Tunables.FilePath;
        RefreshDiagnostics();
        _suppressHandlers = false;
    }

    // ── Diagnostics card ────────────────────────────────────────────
    // Read-only snapshot of the state that is otherwise invisible in a
    // Release build (no log file): attach, session seed, build stamps.
    // No scans, no game writes — only cached values + one cheap process
    // lookup on demand.
    private void RefreshDiagnostics()
    {
        TxtDiagVersion.Text =
            typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("DunDefGame");
            if (procs.Length > 0)
            {
                bool? is32 = GameChain.GameIs32Bit();
                string bits = is32 == true ? "32-bit"
                            : is32 == false ? "64-BIT — UNSUPPORTED (use the 32-bit build)"
                            : "bitness unknown";
                TxtDiagGame.Text = $"running (PID {procs[0].Id}, {bits})";
            }
            else
            {
                TxtDiagGame.Text = "not running";
            }
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }
        catch { TxtDiagGame.Text = "unknown"; }

        if (Window.GetWindow(this) is MainWindow main)
        {
            TxtDiagSeed.Text = main.CurrentPawnVtable != 0
                ? $"0x{main.CurrentPawnVtable:X8}"
                : "not learned yet — happens automatically on the next scan";

            uint recorded = Tunables.GameTimeDateStamp;
            uint live = main.LiveGameStamp;
            string rec = recorded != 0 ? $"0x{recorded:X8}" : "none yet";
            string lv = live != 0 ? $"0x{live:X8}" : "not read yet";
            string verdict = recorded != 0 && live != 0
                ? (recorded == live ? "  (match)" : "  (game updated — will re-learn automatically)")
                : "";
            TxtDiagStamp.Text = $"saved {rec}  ·  game {lv}{verdict}";
        }
    }

    private void BtnDiagRefresh_Click(object sender, RoutedEventArgs e) => RefreshDiagnostics();

    private void BtnCalibrate_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main) return;
        var dlg = new CalibrationDialog(main) { Owner = main };
        dlg.ShowDialog();
        SyncFromState(); // pick up the new pin/status in the panels
    }

    // ── Hotkey buttons ──────────────────────────────────────────────
    // Each button's Tag names which binding it edits. Click starts capture,
    // PreviewKeyDown commits, LostFocus / Esc cancels.

    private void RefreshHotkeyLabels(MainWindow main)
    {
        BtnHkAutoKill.Content = main.Hotkeys.AutoKill.Display();
        BtnHkAutoG.Content = main.Hotkeys.AutoG.Display();
        BtnHkAlwaysOnTop.Content = main.Hotkeys.AlwaysOnTop.Display();
    }

    private void HkButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        _capturingHkButton = b;
        b.Content = "Press combo...";
        b.Focus();
    }

    private void HkButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Button b || _capturingHkButton != b) return;

        // Esc cancels
        if (e.Key == Key.Escape)
        {
            _capturingHkButton = null;
            if (Window.GetWindow(this) is MainWindow m) RefreshHotkeyLabels(m);
            e.Handled = true;
            return;
        }

        // Ignore bare modifier presses — wait for the non-modifier key
        var realKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (realKey is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin
            or Key.System or Key.None)
            return;

        var mods = Keyboard.Modifiers;
        var binding = HotkeyBinding.FromWpf(mods, realKey);
        e.Handled = true;

        if (!binding.HasModifier)
        {
            b.Content = "Need modifier — press combo...";
            return;
        }

        if (Window.GetWindow(this) is MainWindow main)
        {
            switch ((string)b.Tag)
            {
                case "AutoKill": main.Hotkeys.AutoKill = binding; break;
                case "AutoG": main.Hotkeys.AutoG = binding; break;
                case "AlwaysOnTop": main.Hotkeys.AlwaysOnTop = binding; break;
            }
            main.SaveHotkeys();
            RefreshHotkeyLabels(main);
        }
        _capturingHkButton = null;
    }

    private void HkButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && _capturingHkButton == b)
        {
            _capturingHkButton = null;
            if (Window.GetWindow(this) is MainWindow m) RefreshHotkeyLabels(m);
        }
    }

    private void SwFullScanning_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressHandlers) return;
        Base.FullScan = SwFullScanning.IsChecked == true;
    }

    private void SwPauseOnScan_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressHandlers) return;
        Base.PauseScan = SwPauseOnScan.IsChecked == true;
    }

    private void SwSimulateG_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressHandlers) return;
        if (Window.GetWindow(this) is MainWindow main)
            main.SetSimulateG(SwSimulateG.IsChecked == true);
        else
            Base.SimulateG = SwSimulateG.IsChecked == true;
    }

    private void SwAlwaysOnTop_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressHandlers) return;
        if (Window.GetWindow(this) is MainWindow main)
            main.SetAlwaysOnTop(SwAlwaysOnTop.IsChecked == true);
    }

    private void SwAutoKill_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressHandlers) return;
        if (Window.GetWindow(this) is MainWindow main)
            main.SetAutoKillEnabled(SwAutoKill.IsChecked == true);
    }

    private void SwUnlimitedMana_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressHandlers) return;
        if (Window.GetWindow(this) is MainWindow main)
            main.SetUnlimitedMana(SwUnlimitedMana.IsChecked == true);
    }

    private void SwMaxTowerUnits_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressHandlers) return;
        if (Window.GetWindow(this) is MainWindow main)
            main.SetMaxTowerUnits(SwMaxTowerUnits.IsChecked == true);
    }

    private void BtnMoreOptions_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            main.ShowMoreOptionsDialog();
    }

    // ── Memory overrides ────────────────────────────────────────────
    // Re-read the optional file and refresh the displayed status. This
    // validates "does the file parse / what would it apply" — the live
    // session keeps its current values (the override is read at startup;
    // edits take effect on next launch, per the panel description).
    private void BtnOverrideReload_Click(object sender, RoutedEventArgs e)
    {
        Tunables.Reload();
        SyncFromState();
    }

    // Write a template populated from the current effective values, so
    // the file is never hand-authored from a generic example.
    private void BtnOverrideTemplate_Click(object sender, RoutedEventArgs e)
    {
        string? path = Tunables.WriteTemplate();
        Tunables.Reload();
        SyncFromState();
        TxtOverrideStatus.Text = path != null
            ? "Template written — " + Tunables.Status
            : "Could not write template (check folder permissions).";
    }

    private void BtnOverrideOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Tunables.FilePath)!;
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch { /* opening Explorer is best-effort; never crash Settings */ }
    }
}
