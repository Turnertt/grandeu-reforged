using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Modinator;

public partial class App : Application
{
    private static readonly string CrashLogPath =
        Path.Combine(AppContext.BaseDirectory, "crash_log.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
                WriteCrash("AppDomain.UnhandledException", ev.ExceptionObject as Exception);
            DispatcherUnhandledException += (_, ev) =>
            {
                WriteCrash("Dispatcher.UnhandledException", ev.Exception);
                ev.Handled = false;
            };
            TaskScheduler.UnobservedTaskException += (_, ev) =>
                WriteCrash("TaskScheduler.UnobservedTaskException", ev.Exception);

            WriteCrash("Startup", null);
        }

        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

        // Subscribe to Base events for message boxes
        Base.OnMessage += (message, title) =>
        {
            MessageBox.Show(message, title, MessageBoxButton.OK);
        };

        // Process chooser dialog when multiple game instances exist
        Base.OnChooseProcess += (processes) =>
        {
            var dlg = new Views.ChooseProcessDialog(processes);
            dlg.Owner = Current.MainWindow;
            if (dlg.ShowDialog() == true)
                return dlg.SelectedProcess;
            return null;
        };

        base.OnStartup(e);
    }

    private static void WriteCrash(string kind, Exception? ex)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== {DateTime.Now:O} | {kind} | arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} | os={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} ===");
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                sb.AppendLine($"{cur.GetType().FullName}: {cur.Message}");
                sb.AppendLine(cur.StackTrace);
            }
            sb.AppendLine();
            File.AppendAllText(CrashLogPath, sb.ToString());
        }
        catch { }
    }
}
