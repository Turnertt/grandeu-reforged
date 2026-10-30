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
        //
        // Notices are toasts now (Views/Toast.cs): non-blocking, deduplicated
        // and capped, so a repeating failure cannot pile up. Only text too
        // long for a toast (the diagnostic report) still opens a box, and
        // that path keeps the one-at-a-time guard.
        Base.OnMessage += (message, title) =>
        {
            message ??= "";
            if (message.Length <= 600 && message.Count(c => c == '\n') <= 10)
            {
                Views.Toast.Show(message, KindOf(message, title), title);
                return;
            }
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

    // Base.RaiseMessage carries only text and a title (Models stays free of
    // UI types), so the toast's color is read off the wording.
    private static Views.ToastKind KindOf(string message, string? title)
    {
        const StringComparison Ignore = StringComparison.OrdinalIgnoreCase;
        string t = title ?? "";
        if (t.Contains("error", Ignore) || t.Contains("invalid", Ignore)) return Views.ToastKind.Error;
        string[] failures = ["Couldn't", "Could not", "Failed", "Unable", "None of", "That item could not", "Nothing to", "Did you set"];
        if (failures.Any(f => message.StartsWith(f, Ignore))) return Views.ToastKind.Error;
        string[] successes = ["Exported", "Backup saved", "Bulk edit complete", "Written to"];
        if (successes.Any(s => message.StartsWith(s, Ignore))) return Views.ToastKind.Success;
        return Views.ToastKind.Info;
    }

    // 1 while a Base.OnMessage dialog is on screen. int + Interlocked rather
    // than bool: OnMessage can be raised from a background thread.
    private static int _messageBoxOpen;

    protected override void OnExit(ExitEventArgs e)
    {
        // The marker the next session looks for: without it, this session
        // crashed or was killed (Base.BeginSession reports that).
        Base.EndSession();
        base.OnExit(e);
    }

    // Everything here comes from the OS and the tool's own files — never
    // game memory — so the header is written even when nothing else works.
    // The " | admin " token on the first line also tells the next session
    // this log carries the clean-exit marker (Base.EndedCleanly).
    private static string BuildSessionHeader()
    {
        string ver = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        string header = "=== GrandeuReforged " + ver
             + " | " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
             + " | Windows " + Environment.OSVersion.Version
             + " | admin " + (IsElevated() ? "yes" : "no")
             + " | " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===";
        try
        {
            string? wine = WineVersion();
            header += "\nhost: " + (wine != null ? "Wine/Proton " + wine : "Windows (not Wine)")
                    + " | " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                    + " | exe " + (Environment.ProcessPath ?? "?");
        }
        catch { }
        try
        {
            header += "\nsaved addresses: "
                    + (File.Exists(Tunables.FilePath)
                        ? (Tunables.GameTimeDateStamp != 0 ? $"present, pinned for game build 0x{Tunables.GameTimeDateStamp:X8}" : "present, no game build recorded")
                        : "none yet")
                    + " | last game build seen: " + (Prefs.Current.LastGameBuild ?? "none yet");
        }
        catch { }
        return header;
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // Wine exports wine_get_version from its ntdll; real Windows doesn't.
    // The standard detection — no file or registry sniffing.
    [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);
    [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate IntPtr WineGetVersion();

    private static string? WineVersion()
    {
        try
        {
            IntPtr ntdll = GetModuleHandle("ntdll.dll");
            if (ntdll == IntPtr.Zero) return null;
            IntPtr fn = GetProcAddress(ntdll, "wine_get_version");
            if (fn == IntPtr.Zero) return null;
            var get = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<WineGetVersion>(fn);
            return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(get()) ?? "unknown";
        }
        catch { return null; }
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
