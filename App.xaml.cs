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
        // One log per run. Truncating here is what makes "send me your log"
        // produce the session that went wrong rather than an accumulated pile.
        Base.BeginSession(BuildSessionHeader());

        // Crash handlers on EVERY architecture. These used to be registered
        // only on ARM64, so on the shipped x86 build an unhandled exception
        // took the app down leaving nothing to send — which is precisely the
        // situation the log exists for.
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            LogFatal("AppDomain.UnhandledException", ev.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, ev) =>
        {
            LogFatal("Dispatcher.UnhandledException", ev.Exception);
            ev.Handled = false;
        };
        TaskScheduler.UnobservedTaskException += (_, ev) =>
            LogFatal("TaskScheduler.UnobservedTaskException", ev.Exception);

        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

        // Subscribe to Base events for message boxes.
        //
        // Hard rule: only ever ONE of these on screen. A MessageBox runs its
        // own message loop, so whatever raised it keeps running behind it —
        // the 50 ms UI timer keeps ticking, background loops keep looping —
        // and the next failure stacks another dialog on top. That was a real
        // softlock: a toggle whose work needs the game would spawn a dialog
        // per second with the game closed, and the user could neither close
        // the app nor switch the toggle back off. Callers on a repeating path
        // should additionally pass notify:false to Base.OpenProcess and
        // report in the UI; this guard is the backstop for every other case.
        Base.OnMessage += (message, title) =>
        {
            if (System.Threading.Interlocked.Exchange(ref _messageBoxOpen, 1) != 0) return;
            try { MessageBox.Show(message, title, MessageBoxButton.OK); }
            finally { System.Threading.Interlocked.Exchange(ref _messageBoxOpen, 0); }
        };

        // Process chooser dialog when multiple game instances exist. Modal, so
        // it shares the one-dialog-at-a-time guard above for the same reason.
        Base.OnChooseProcess += (processes) =>
        {
            if (System.Threading.Interlocked.Exchange(ref _messageBoxOpen, 1) != 0) return null;
            try
            {
                var dlg = new Views.ChooseProcessDialog(processes);
                dlg.Owner = Current.MainWindow;
                if (dlg.ShowDialog() == true)
                    return dlg.SelectedProcess;
                return null;
            }
            finally { System.Threading.Interlocked.Exchange(ref _messageBoxOpen, 0); }
        };

        base.OnStartup(e);
    }

    // 1 while a Base.OnMessage dialog is on screen. int + Interlocked rather
    // than bool: OnMessage can be raised from a background thread.
    private static int _messageBoxOpen;

    private static string BuildSessionHeader()
    {
        string ver = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        return "=== GrandeuReforged " + ver
             + " | " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
             + " | Windows " + Environment.OSVersion.Version
             + " | " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===";
    }

    private static void LogFatal(string kind, Exception? ex)
    {
        Base.LogEvent("FATAL [" + kind + "] " + (ex?.GetType().Name ?? "unknown")
                    + ": " + (ex?.Message ?? "(no message)")
                    + "\n" + (ex?.StackTrace ?? "(no stack)"));
        // The ARM64 crash file stays as a second, independent record.
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64)
            WriteCrash(kind, ex);
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
