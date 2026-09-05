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
    // feature) so the page stays in sync with reality. _suppressHandlers
    // blocks the Toggled events that setting IsChecked would otherwise fire.
    //
    // The gameplay switches deliberately no longer live here — every one of
    // them is a title-bar toggle, and duplicating them meant two controls to
    // keep in sync for no benefit. Settings owns their HOTKEYS instead.
    public void SyncFromState()
    {
        _suppressHandlers = true;
        SwFullScanning.IsChecked = Base.FullScan;
        SwPauseOnScan.IsChecked = Base.PauseScan;
        SwErrorLog.IsChecked = Prefs.Current.ErrorLogEnabled;
        if (Window.GetWindow(this) is MainWindow main)
            RefreshHotkeyLabels(main);
        RefreshStatus();
        _suppressHandlers = false;
    }

    // ── Advanced status values ──────────────────────────────────────
    // A few words each. These sit next to a label inside a card that already
    // explains what the thing is, so the value should not re-explain it —
    // "13 backups", not a sentence. Anything longer belongs in COPY REPORT.
    private void RefreshStatus()
    {
        // Game
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("DunDefGame");
            if (procs.Length > 0)
                TxtStatusGame.Text = GameChain.GameIs32Bit() == false
                    ? "Running — but 64-bit, which isn't supported"
                    : "Running";
            else
                TxtStatusGame.Text = "Not running";
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }
        catch { TxtStatusGame.Text = "Unknown"; }

        // Addresses — "learned" vs "default" without showing anyone a hex offset.
        try
        {
            bool haveSeed = Window.GetWindow(this) is MainWindow m && m.CurrentPawnVtable != 0;
            int moved = 0;
            if (GameChain.ItemBoxOffset != Tunables.DefaultItemBoxOffset) moved++;
            if (GameChain.LocalHeroesOffset != Tunables.DefaultLocalHeroesOffset) moved++;
            if (GameChain.HeroManagerOffset != Tunables.DefaultHeroManagerOffset) moved++;

            TxtStatusAddresses.Text = !haveSeed ? "Not learned yet"
                                    : moved == 0 ? "Up to date"
                                    : "Up to date (" + moved + " relocated)";
        }
        catch { TxtStatusAddresses.Text = "Unknown"; }

        RefreshBackupStatus();
        RefreshLogStatus();
        RefreshAddressFileStatus();
    }

    // The toggle beside the card title already says on/off — repeating it here
    // is what made the old layout state it twice. Show only the size.
    private void RefreshLogStatus()
    {
        if (!Prefs.Current.ErrorLogEnabled)
        {
            TxtStatusLog.Text = "Not being kept";
            return;
        }
        try
        {
            var fi = new System.IO.FileInfo(Base.LogPath);
            TxtStatusLog.Text = fi.Exists ? FormatSize(fi.Length) + " this session" : "Empty so far";
        }
        catch { TxtStatusLog.Text = "Empty so far"; }
    }

    private void RefreshAddressFileStatus()
    {
        try
        {
            TxtStatusAddrFile.Text = System.IO.File.Exists(Tunables.FilePath)
                ? "Saved" : "Not written yet";
        }
        catch { TxtStatusAddrFile.Text = "Unknown"; }
    }

    private static string FormatSize(long bytes)
        => bytes < 1024 ? bytes + " bytes"
         : bytes < 1024 * 1024 ? (bytes / 1024) + " KB"
         : (bytes / (1024 * 1024)) + " MB";

    private void RefreshBackupStatus()
    {
        try
        {
            BtnResetSaveFolder.Visibility = string.IsNullOrWhiteSpace(Prefs.Current.SaveFolderOverride)
                ? Visibility.Collapsed : Visibility.Visible;

            string? folder = SaveBackup.ResolveSaveFolder(out _);
            if (folder == null)
            {
                TxtStatusBackups.Text = "Save folder not found";
                TxtStatusSession.Text = "Skipped until the folder is set";
                return;
            }

            var list = SaveBackup.ListBackups();
            TxtStatusBackups.Text = list.Count == 0
                ? "None yet"
                : list.Count + (list.Count == 1 ? " backup" : " backups")
                  + ", newest " + Describe(list[0].CreatedLocal);

            TxtStatusSession.Text = SaveBackup.SessionBackupDone
                ? "Backed up"
                : "Not yet — happens before your next edit";
            if (SaveBackup.LastError != null)
                TxtStatusSession.Text += "\n" + SaveBackup.LastError;
        }
        catch (System.Exception ex)
        {
            TxtStatusBackups.Text = "Couldn't check";
            TxtStatusSession.Text = ex.Message;
        }
    }

    // "3 minutes ago" reads better than a timestamp for the one thing users
    // actually want to know: is my backup recent enough to rely on.
    private static string Describe(System.DateTime when)
    {
        var ago = System.DateTime.Now - when;
        if (ago.TotalMinutes < 1) return "just now";
        if (ago.TotalMinutes < 60) return (int)ago.TotalMinutes + " min ago";
        if (ago.TotalHours < 24) return (int)ago.TotalHours + " hour" + ((int)ago.TotalHours == 1 ? "" : "s") + " ago";
        if (ago.TotalDays < 30) return (int)ago.TotalDays + " day" + ((int)ago.TotalDays == 1 ? "" : "s") + " ago";
        return when.ToString("yyyy-MM-dd");
    }

    private void BtnDiagRefresh_Click(object sender, RoutedEventArgs e) => RefreshStatus();

    private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Base.LogPath)!;
            System.IO.Directory.CreateDirectory(dir);
            // Select the file rather than just opening the folder, so the one
            // to attach is unambiguous among the other files that live there.
            if (System.IO.File.Exists(Base.LogPath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", "/select,\"" + Base.LogPath + "\"") { UseShellExecute = true });
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { /* opening Explorer is best-effort; never crash Settings */ }
    }

    private void SwErrorLog_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressHandlers) return;
        Prefs.Current.ErrorLogEnabled = SwErrorLog.IsChecked == true;
        Prefs.Current.Save();
        if (Prefs.Current.ErrorLogEnabled)
            Base.LogEvent("Error log re-enabled from Settings.");
        RefreshLogStatus();
    }

    // ── Save backups ────────────────────────────────────────────────

    private void BtnBackupNow_Click(object sender, RoutedEventArgs e)
    {
        var b = SaveBackup.CreateBackup("manual", skipIfUnchanged: false);
        Base.RaiseMessage(
            b != null ? "Backup saved to:\n" + b.Folder
                      : (SaveBackup.LastError ?? "Backup failed."),
            "Save Backups");
        RefreshBackupStatus();
    }

    private void BtnBackupRestore_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RestoreBackupDialog { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        RefreshBackupStatus();
    }

    private void BtnBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(SaveBackup.BackupRoot);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + SaveBackup.BackupRoot + "\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void BtnChangeSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pick the folder that contains DunDefHeroes.dun",
        };
        string? cur = SaveBackup.ResolveSaveFolder(out _);
        if (cur != null) dlg.InitialDirectory = cur;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        string picked = dlg.FolderName;
        if (!System.IO.File.Exists(System.IO.Path.Combine(picked, SaveBackup.SaveFileName)))
        {
            Base.RaiseMessage(
                "That folder doesn't contain " + SaveBackup.SaveFileName + ".\n\n" +
                "The Steam save normally lives in Steam\\userdata\\<account>\\65800\\remote.",
                "Save Backups");
            return;
        }
        Prefs.Current.SaveFolderOverride = picked;
        Prefs.Current.Save();
        RefreshBackupStatus();
    }

    private void BtnResetSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        Prefs.Current.SaveFolderOverride = null;
        Prefs.Current.Save();
        RefreshBackupStatus();
    }

    private void BtnViewDisclaimer_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new DisclaimerDialog(reviewOnly: true) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    // Read-only chain dump → clipboard, for "it works here but not there".
    private async void BtnCopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main) return;
        if (sender is not Button btn) return;
        // Attach on the UI thread first (may raise the choose-process dialog):
        // the report's HeroManager window dump reads through Base.Instance,
        // which is unattached if no scan has run this session.
        try { Base.OpenProcess(); } catch { /* reported inside the dump */ }
        object? label = btn.Content;
        btn.IsEnabled = false;
        btn.Content = "WORKING...";
        try
        {
            // The report can run a full structural sweep on a cold cache —
            // same rule as the scans, keep it off the UI thread.
            string text = await System.Threading.Tasks.Task.Run(main.BuildDiagnosticReport);
            try { Clipboard.SetText(text); }
            catch { /* clipboard can transiently fail when another app holds it */ }
            Base.RaiseMessage(text + "\n\n(copied to the clipboard)", "Diagnostic report");
        }
        catch (System.Exception ex)
        {
            Base.RaiseMessage("Couldn't build the report: " + ex.Message, "Diagnostic report");
        }
        finally { btn.Content = label; btn.IsEnabled = true; }
    }

    private void BtnCalibrate_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main) return;
        var dlg = new CalibrationDialog(main) { Owner = main };
        dlg.ShowDialog();
        SyncFromState(); // pick up the new pin/status
    }

    // ── Hotkey buttons ──────────────────────────────────────────────
    // Each button's Tag names which binding it edits. Click starts capture,
    // PreviewKeyDown commits, LostFocus / Esc cancels.

    private void RefreshHotkeyLabels(MainWindow main)
    {
        BtnHkAutoKill.Content = main.Hotkeys.AutoKill.Display();
        BtnHkAutoG.Content = main.Hotkeys.AutoG.Display();
        BtnHkAlwaysOnTop.Content = main.Hotkeys.AlwaysOnTop.Display();
        BtnHkUnlimitedMana.Content = main.Hotkeys.UnlimitedMana.Display();
        BtnHkMaxTowerUnits.Content = main.Hotkeys.MaxTowerUnits.Display();
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
                case "UnlimitedMana": main.Hotkeys.UnlimitedMana = binding; break;
                case "MaxTowerUnits": main.Hotkeys.MaxTowerUnits = binding; break;
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

    // ── Legacy ──────────────────────────────────────────────────────

    private void BtnLegacyToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = LegacyPanel.Visibility != Visibility.Visible;
        LegacyPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BtnLegacyToggle.Content = show ? "HIDE" : "SHOW";
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

    private void BtnMoreOptions_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            main.ShowMoreOptionsDialog();
    }

    // Re-read the optional overrides file and refresh the displayed status.
    // The live session keeps its current values (the file is read at startup;
    // hand edits take effect on the next launch).
    private void BtnOverrideReload_Click(object sender, RoutedEventArgs e)
    {
        Tunables.Reload();
        SyncFromState();
    }

    // Write a template populated from the current effective values, so the
    // file is never hand-authored from a generic example.
    private void BtnOverrideTemplate_Click(object sender, RoutedEventArgs e)
    {
        string? path = Tunables.WriteTemplate();
        Tunables.Reload();
        SyncFromState();
        Base.RaiseMessage(
            path != null
                ? "Written to:\n" + path + "\n\n" + Tunables.Status
                : "Could not write the template (check folder permissions).",
            "Saved addresses");
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
