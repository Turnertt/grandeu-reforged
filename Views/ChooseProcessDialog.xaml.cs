using System.Diagnostics;
using System.Windows;

namespace Modinator.Views;

public partial class ChooseProcessDialog : Window
{
    public Process? SelectedProcess { get; private set; }

    public ChooseProcessDialog(Process[] processes)
    {
        InitializeComponent();
        foreach (var p in processes)
        {
            ProcessList.Items.Add(new ProcessEntry
            {
                PID = p.Id.ToString(),
                StartTime = p.StartTime.ToString("h:mm:ss tt"),
                WindowTitle = p.MainWindowTitle,
                Process = p
            });
        }
        if (ProcessList.Items.Count > 0)
            ProcessList.SelectedIndex = 0;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is ProcessEntry entry)
        {
            SelectedProcess = entry.Process;
            DialogResult = true;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

internal class ProcessEntry
{
    public string PID { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public Process? Process { get; set; }
}
