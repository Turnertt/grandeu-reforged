using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace Modinator;

// Win32 modifier flags used by RegisterHotKey — kept as uints so we can push
// them straight into the P/Invoke without shuffling enums at the boundary.
public static class HotkeyMods
{
    public const uint NONE = 0x0000;
    public const uint ALT = 0x0001;
    public const uint CONTROL = 0x0002;
    public const uint SHIFT = 0x0004;
    public const uint WIN = 0x0008;
}

public record HotkeyBinding(uint Modifiers, uint VirtualKey)
{
    // WPF Key -> Win32 virtual-key translation. KeyInterop handles the normal
    // mapping; we normalize the handful of modifier-ish keys to 0 so we never
    // bind "just Ctrl" as a standalone hotkey.
    public static HotkeyBinding FromWpf(ModifierKeys mods, Key key)
    {
        uint m = 0;
        if ((mods & ModifierKeys.Control) != 0) m |= HotkeyMods.CONTROL;
        if ((mods & ModifierKeys.Shift) != 0) m |= HotkeyMods.SHIFT;
        if ((mods & ModifierKeys.Alt) != 0) m |= HotkeyMods.ALT;
        if ((mods & ModifierKeys.Windows) != 0) m |= HotkeyMods.WIN;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return new HotkeyBinding(m, vk);
    }

    public bool HasModifier => Modifiers != 0;

    public string Display()
    {
        if (VirtualKey == 0) return "(unbound)";
        var parts = new List<string>();
        if ((Modifiers & HotkeyMods.CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & HotkeyMods.SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & HotkeyMods.ALT) != 0) parts.Add("Alt");
        if ((Modifiers & HotkeyMods.WIN) != 0) parts.Add("Win");
        var k = KeyInterop.KeyFromVirtualKey((int)VirtualKey);
        parts.Add(k.ToString());
        return string.Join("+", parts);
    }
}

public class HotkeyConfig
{
    public HotkeyBinding AutoKill { get; set; } = new(HotkeyMods.CONTROL | HotkeyMods.SHIFT, (uint)KeyInterop.VirtualKeyFromKey(Key.K));
    public HotkeyBinding AutoG { get; set; } = new(HotkeyMods.CONTROL | HotkeyMods.SHIFT, (uint)KeyInterop.VirtualKeyFromKey(Key.G));
    public HotkeyBinding AlwaysOnTop { get; set; } = new(HotkeyMods.CONTROL | HotkeyMods.SHIFT, (uint)KeyInterop.VirtualKeyFromKey(Key.T));
    public HotkeyBinding UnlimitedMana { get; set; } = new(HotkeyMods.CONTROL | HotkeyMods.SHIFT, (uint)KeyInterop.VirtualKeyFromKey(Key.M));
    public HotkeyBinding MaxTowerUnits { get; set; } = new(HotkeyMods.CONTROL | HotkeyMods.SHIFT, (uint)KeyInterop.VirtualKeyFromKey(Key.U));

    private static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Modinator", "hotkeys.json");

    public static HotkeyConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                string json = File.ReadAllText(Path);
                var cfg = JsonSerializer.Deserialize<HotkeyConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new HotkeyConfig();
    }

    public void Save()
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
