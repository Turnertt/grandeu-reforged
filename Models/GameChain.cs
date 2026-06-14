using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Modinator;

// Shared, read-only walk of the HeroManager pointer chain + small game
// memory helpers. One home for what ForgeViewerView, HeroViewerView and
// the Settings calibration wizard previously each duplicated. All reads
// via Base.Instance (the sanctioned Scanner path); never writes.
//
// Chain (DD1_INTERNALS.md §3, verified live across builds):
//   playerPawn +0x22C → ADunDefPlayerController
//              +0x3B8 → Player (ULocalPlayer)
//              +0x194 → ViewportClient (UDunDefViewportClient)
//              +0xCFC → TheHeroManager (UDunDefHeroManager)
internal static class GameChain
{
    public const int OFF_PAWN_CONTROLLER     = 0x22C;
    public const int OFF_CONTROLLER_PLAYER   = 0x3B8;
    public const int OFF_PLAYER_VIEWPORT     = 0x194;
    public const int OFF_VIEWPORT_HEROMGR    = 0xCFC;
    // LocalLoadedHeroes (+0x360) = the player's FULL local hero roster
    // (all saved heroes). It's a TArray<TScriptInterface<...>> — 8 bytes
    // per element, first dword is the UDunDefHero*. ActiveHeroes (+0x36C)
    // is only the in-play hero(es). Verified live 2026-06-11 via the DLL
    // memdump: +0x360 yields all 11 heroes, +0x36C yields just one.
    public const int OFF_HM_LOCALHEROES      = 0x360; // TArray<TScriptInterface>, stride 8
    public const int OFF_HM_ACTIVEHEROES     = 0x36C; // TArray<UDunDefHero*>, stride 4
    public const int OFF_HM_ITEMBOX          = 0x39C; // TArray<UHeroEquipment*>

    public static int ResolveHeroManager(int playerPawn)
    {
        if (!IsGamePtr(playerPawn)) return 0;
        int controller = RdPtr(playerPawn + OFF_PAWN_CONTROLLER);
        int player     = RdPtr(controller + OFF_CONTROLLER_PLAYER);
        int vpClient   = RdPtr(player + OFF_PLAYER_VIEWPORT);
        return RdPtr(vpClient + OFF_VIEWPORT_HEROMGR);
    }

    // Reads a UE3 TArray<T*> header (data ptr, Num, Max) and returns the
    // live element pointers. Defensive Num cap; bad reads yield an empty
    // list (a hero with no equipment, etc.).
    public static List<int> ReadPtrArray(int tarrayAddr) => ReadPtrArray(tarrayAddr, 4);

    // Stride-aware variant: each element is `stride` bytes and the element
    // pointer is its first dword. stride 4 = TArray<T*>; stride 8 =
    // TArray<TScriptInterface> (the {Object*, Interface*} pair, Object*
    // first) — e.g. LocalLoadedHeroes.
    public static List<int> ReadPtrArray(int tarrayAddr, int stride)
    {
        var result = new List<int>();
        int dataPtr = RdPtr(tarrayAddr);
        int num     = RdInt(tarrayAddr + 4);
        if (!IsGamePtr(dataPtr) || num <= 0 || num > 200000) return result;

        byte[]? arr;
        try { arr = Base.Instance.ReadMemory(dataPtr, num * stride); }
        catch { return result; }
        if (arr == null || arr.Length < num * stride) return result;

        for (int i = 0; i < num; i++)
        {
            int p = System.BitConverter.ToInt32(arr, i * stride);
            if (IsGamePtr(p)) result.Add(p);
        }
        return result;
    }

    // UE3 FString: data ptr, ArrayNum (chars incl. null), ArrayMax.
    public static string ReadFString(int addr)
    {
        try
        {
            int ptr = RdPtr(addr);
            int num = RdInt(addr + 4);
            if (!IsGamePtr(ptr) || num <= 1 || num > 256) return "";
            byte[]? b = Base.Instance.ReadMemory(ptr, (num - 1) * 2);
            if (b == null) return "";
            return System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0');
        }
        catch { return ""; }
    }

    // DD1 is LARGEADDRESSAWARE on WOW64 — heap sits anywhere in
    // [0x01000000, 0xFFFE0000). Matches MainWindow.IsHeapPtr.
    public static bool IsGamePtr(int p)
        => (uint)p >= 0x01000000u && (uint)p < 0xFFFE0000u;

    public static int RdPtr(int addr)
    {
        if (!IsGamePtr(addr)) return 0;
        return RdInt(addr);
    }

    public static int RdInt(int addr)
    {
        try
        {
            byte[] b = Base.Instance.ReadMemory(addr, 4);
            return (b != null && b.Length >= 4) ? System.BitConverter.ToInt32(b, 0) : 0;
        }
        catch { return 0; }
    }

    // ── Game bitness ────────────────────────────────────────────────
    // The tool's whole memory model (4-byte pointers, x86 P/Invoke,
    // 0x00400000 base) only fits the 32-bit DD1 build. A 64-bit game
    // process is a hard "unsupported", and the single most useful thing
    // a failed scan can say.

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern System.IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(System.IntPtr h, out bool wow64);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(System.IntPtr h);

    // true = 32-bit game (supported), false = 64-bit game (unsupported),
    // null = game not running / undeterminable.
    public static bool? GameIs32Bit()
    {
        int pid = 0;
        var procs = System.Diagnostics.Process.GetProcessesByName("DunDefGame");
        if (procs.Length > 0) pid = procs[0].Id;
        foreach (var p in procs) { try { p.Dispose(); } catch { } }
        if (pid == 0) return null;

        // 32-bit OS can't run a 64-bit game at all.
        if (!System.Environment.Is64BitOperatingSystem) return true;

        System.IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == System.IntPtr.Zero) return null;
        try
        {
            if (!IsWow64Process(h, out bool wow64)) return null;
            return wow64; // WOW64 ⇒ 32-bit game on a 64-bit OS
        }
        finally { CloseHandle(h); }
    }

    // One staged, human-readable reason for a failed scan. `playerPawn`
    // is whatever the caller's pawn resolution returned (0 = none).
    public static string DescribeScanFailure(int playerPawn)
    {
        bool? is32 = GameIs32Bit();
        if (is32 == null)
            return "DunDefGame.exe is not running. Start Dungeon Defenders first.";
        if (is32 == false)
            return "This tool only works on the 32-bit version of Dungeon Defenders — " +
                   "the running game is 64-bit. Switch to the 32-bit build and rescan.";
        if (playerPawn == 0)
            return "No character found — the game looks like it's in a menu or loading screen. " +
                   "Go to the Tavern or a mission, then rescan. " +
                   "If that doesn't help, run CALIBRATE in Settings.";
        return "Couldn't reach the item manager from the player character " +
               "(a transient stale read is common right after a map change). " +
               "Rescan in a few seconds, or run CALIBRATE in Settings.";
    }
}
