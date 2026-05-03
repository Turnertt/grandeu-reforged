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
            RefreshHotkeyLabels(main);
        }
        _suppressHandlers = false;
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

    private void BtnMoreOptions_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            main.ShowMoreOptionsDialog();
    }
}
