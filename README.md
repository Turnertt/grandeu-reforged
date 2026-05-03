# Grandeu: Reforged — Source

This folder contains the complete source code for **Grandeu: Reforged**, a memory editor for *Dungeon Defenders 1* (`DunDefGame.exe`). It is a WPF rewrite of the original WinForms tool *Grandeu 4.0.0.4*. The shipped binary is `GrandeuReforged.exe`.

## Why does the binary trigger antivirus?

The application reads and writes another process's memory using the standard Windows APIs `OpenProcess`, `ReadProcessMemory`, `WriteProcessMemory`, `VirtualQueryEx`, `VirtualAllocEx`, and `VirtualFreeEx` (all P/Invoked from `kernel32.dll`). These are the same APIs used by debuggers, profilers, and other game-modding tools — and they are also commonly used by malware, which is why generic-heuristic AV engines flag any unsigned executable that uses them.

The source in this folder is the entire program. There is no network code, no persistence outside the user's own settings file, no auto-updater, no DLL injection, and no game-file modification. The complete list of P/Invokes can be reviewed in:

- `Models/Scanner.cs` — the general memory scanner
- `MainWindow.xaml.cs` (search for `[DllImport]`) — the parallel auto-kill memory path

## Why is the executable named `GrandeuReforged.exe` and not `Grandeu.exe`?

Dungeon Defenders' built-in anti-cheat scans running process names for the exact filename `Grandeu.exe` and, if found, blocks the user's session. The `<AssemblyName>` was changed to `GrandeuReforged` to avoid that exact-match check. The internal branding (window title, About dialog) still reads "Grandeu: Reforged".

## Build requirements

- **Windows 10 or 11**
- **.NET 8 SDK** (x86 or x64 host is fine — the build targets x86) — https://dotnet.microsoft.com/download/dotnet/8.0
- No third-party NuGet packages are referenced; the only dependencies are WPF and the Windows base class library.

## Quick build (one command)

From the folder containing this README, run **either**:

```powershell
# PowerShell
.\build.ps1
```

```cmd
:: cmd.exe
build.bat
```

Both wrappers do the same thing: build a release single-file executable and copy it to `output\GrandeuReforged.exe`.

## Manual build

```powershell
dotnet publish Modinator.csproj -c Release -r win-x86 -p:SelfContained=false -p:PublishSingleFile=true
```

The output will land at:

```
bin\Release\net8.0-windows\win-x86\publish\GrandeuReforged.exe
```

This is the file that gets shipped. End-users need the **.NET 8 Desktop Runtime (x86)** installed (https://dotnet.microsoft.com/download/dotnet/8.0). The runtime is intentionally not bundled to keep the executable small (~820 KB).

### Build gotchas

- `-p:SelfContained=false` **must** be passed as an MSBuild property (with the `-p:` prefix), not as the `--self-contained false` CLI flag. Recent SDK versions silently ignore the CLI flag when `-r` is specified and produce a self-contained ~61 MB executable instead.
- Do **not** add `-p:EnableCompressionInSingleFile=true`. Single-file compression requires `SelfContained=true`; the SDK will hard-error `NETSDK1176` otherwise.
- The intermediate publish folder also contains `GrandeuReforged.pdb` (debug symbols). Ship only the `.exe`.

## Optional: ARM64 build (Windows on ARM)

```powershell
dotnet publish Modinator.csproj -c Release -r win-arm64 -p:SelfContained=false -p:PublishSingleFile=true -p:PlatformTarget=ARM64
```

Output: `bin\Release\net8.0-windows\win-arm64\publish\GrandeuReforged.exe`. End-users on ARM64 need the **.NET 8 Desktop Runtime ARM64** installed.

## Running the program

1. Launch *Dungeon Defenders 1* first.
2. Run `GrandeuReforged.exe`. Administrator privileges may be required so it can open a handle to the game process.
3. Pick a tool from the left sidebar.

## Project layout

```
Modinator.csproj      Project file (target: net8.0-windows, platform: x86, WPF)
App.xaml(.cs)         WPF entry point
AssemblyInfo.cs       Standard assembly metadata
MainWindow.xaml(.cs)  Main window + sidebar + auto-kill memory loop
Models/               Plain C# — game memory layout (no WPF dependency)
  Base.cs               Static facade over Scanner (events, options, helpers)
  Scanner.cs            P/Invoke memory read/write/scan
  *Native.cs            [StructLayout] structs matching DD1 in-memory layout
  *User.cs / *Search.cs Friendlier projections + per-genus search params
Views/                WPF user controls — search forms, edit forms, dialogs
Themes/               WPF resource dictionaries (colors, button/textbox styles, frameless window chrome)
Behaviors/            Small attached behaviors (numeric input, placeholder text)
Assets/               app-icon.ico/png + per-stat icons used in the editor
build.ps1 / build.bat One-shot build wrappers (call `dotnet publish` with the right flags)
```

## License

All original code in this folder is released for free use. The decompiled WinForms predecessor (*Grandeu 4.0.0.4*) is credited as the structural starting point; struct offsets and reverse-engineering notes were informed by the *DD_ModMenu* project and the broader DD1 modding community.
