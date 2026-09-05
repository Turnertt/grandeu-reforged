using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Modinator.Views;

// Settings → Save Backups → RESTORE. Lists the dated backups under
// %LOCALAPPDATA%\Modinator\save-backups and copies the chosen one back
// over the game's save folder (SaveBackup.Restore does the checks: game
// closed, pre-restore backup first).
public partial class RestoreBackupDialog : Window
{
    private sealed class Row
    {
        public BackupInfo Info = null!;
        public string Created => Info.CreatedLocal.ToString("yyyy-MM-dd  HH:mm:ss");
        public string Kind => Info.KindLabel;
        public string SaveWritten => Info.SaveWriteTimeUtc == DateTime.MinValue
            ? "—" : Info.SaveWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd  HH:mm:ss");
        public string Size => Info.Bytes >= 1024 * 1024
            ? (Info.Bytes / 1024.0 / 1024.0).ToString("0.0") + " MB"
            : (Info.Bytes / 1024.0).ToString("0") + " KB";
        public string Files => Info.FileCount.ToString();
    }

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public bool Restored { get; private set; }

    public RestoreBackupDialog()
    {
        InitializeComponent();
        Reload();
        UpdateGameState();
        _poll.Tick += (_, _) => UpdateGameState();
        _poll.Start();
        Closed += (_, _) => _poll.Stop();
    }

    private void Reload()
    {
        var rows = new List<Row>();
        foreach (var b in SaveBackup.ListBackups()) rows.Add(new Row { Info = b });
        BackupList.ItemsSource = rows;
        LblEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count > 0) BackupList.SelectedIndex = 0;
        UpdateButtons();
    }

    private bool _gameRunning;

    private void UpdateGameState()
    {
        _gameRunning = SaveBackup.GameRunning();
        LblGameState.Text = _gameRunning
            ? "Dungeon Defenders is running — close it before restoring."
            : "Game closed — ready to restore.";
        LblGameState.Foreground = (System.Windows.Media.Brush)FindResource(_gameRunning ? "DangerBrush" : "TextMutedBrush");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        BtnRestore.IsEnabled = !_gameRunning && BackupList.SelectedItem is Row;
    }

    private void BackupList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        string folder = BackupList.SelectedItem is Row r ? r.Info.Folder : SaveBackup.BackupRoot;
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not Row r) return;
        var ok = MessageBox.Show(this,
            $"Replace the game's save with the backup from {r.Created} ({r.Kind})?\n\n" +
            "The current save is copied to a 'Before restore' backup first, so this can be undone.",
            "Restore save", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes) return;

        string? err = SaveBackup.Restore(r.Info);
        if (err != null)
        {
            MessageBox.Show(this, err, "Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateGameState();
            return;
        }
        Restored = true;
        MessageBox.Show(this,
            "Save restored. Start Dungeon Defenders to load it.\n\n" +
            "If Steam shows a Cloud sync conflict, choose the local (newer) file.",
            "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
        Reload();
    }
}
