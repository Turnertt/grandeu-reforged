using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Modinator.Themes;

// Registers process-global hotkeys via user32 RegisterHotKey. Dispatches on
// WM_HOTKEY inside the owner window's wnd-proc, so the hotkey fires even when
// DunDefGame has keyboard focus. Be careful about collisions — a registered
// combo is *consumed* by us and won't reach the game.
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;

    private readonly Window _window;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private readonly Dictionary<int, Action> _callbacks = new();

    public HotkeyManager(Window window)
    {
        _window = window;
        // HwndSource is only available after the window has an HWND.
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Attach();
        else
            window.SourceInitialized += (_, _) => Attach();
    }

    private void Attach()
    {
        _hwnd = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    public bool Register(int id, HotkeyBinding binding, Action callback)
    {
        Unregister(id);
        if (binding.VirtualKey == 0 || !binding.HasModifier) return false;
        if (_hwnd == IntPtr.Zero) return false;
        if (!RegisterHotKey(_hwnd, id, binding.Modifiers, binding.VirtualKey)) return false;
        _callbacks[id] = callback;
        return true;
    }

    public void Unregister(int id)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_callbacks.ContainsKey(id))
        {
            UnregisterHotKey(_hwnd, id);
            _callbacks.Remove(id);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out var cb))
            {
                try { cb(); } catch { /* callbacks are best-effort */ }
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _callbacks.Keys.ToList()) Unregister(id);
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
