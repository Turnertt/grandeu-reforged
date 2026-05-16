<p align="center">
  <img src="Assets/app-icon.png" alt="Grandeu: Reforged icon" width="128" />
</p>

# Grandeu: Reforged

A memory editor for *Dungeon Defenders 1* (`DunDefGame.exe`) — a WPF rewrite of the original WinForms tool *Grandeu 4.0.0.4*. The shipped binary is `GrandeuReforged.exe`.

![Screenshot of the home screen](docs/screenshot.png)

## Features

- **Forge Viewer** — browse your forge inventory and pick items to modify in seconds. Filter by **Source** (All / Forge / Hero) with real folder names.
- **Max Stat** — one-click max out any item's stats. **Class-aware**: only applies the stats valid for that item and weapon family. Works with Bulk Edit on any mix of item types.
- **Item Dupe** — copy an item's stats onto another item, using the game's own value set for the lowest crash risk.
- **Bulk Edit** — modify up to 100 items at once.
- **Auto Kill** — flip a switch to instantly clear enemies from the map. Multiplayer-safe hero protection covers every hero class, including DLC heroes, Summoner pets, and Series EV turrets.
- **Unlimited Mana / Max Tower Units** — in-level title-bar toggles.
- **Game Speed Control** — accelerate the game to blast through levels.
- **Hero Editor** — customize hero stats and appearance, including color.
- **Item Editor** — full control over item stats, affixes, and properties, including a read-only weapon-class indicator.
- **Modern UI** — clean dark theme, sidebar navigation, touch- and small-screen-friendly scrollbars.

## Build requirements

- **Windows 10 or 11**
- **.NET 8 SDK** — https://dotnet.microsoft.com/download/dotnet/8.0
- No third-party NuGet packages. Only dependencies are WPF and the Windows base class library.

## Quick build (one command)

From the repo root:

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

Output:

```
bin\Release\net8.0-windows\win-x86\publish\GrandeuReforged.exe
```

End-users need the **.NET 8 Desktop Runtime (x86)** installed (https://dotnet.microsoft.com/download/dotnet/8.0). The runtime is intentionally not bundled to keep the binary small (~820 KB).

### Build notes

- `-p:SelfContained=false` **must** be passed as an MSBuild property (`-p:` prefix), not as the `--self-contained false` CLI flag. Recent SDKs silently ignore the CLI flag when `-r` is specified and produce a self-contained ~61 MB executable instead.
- Do **not** add `-p:EnableCompressionInSingleFile=true`. Compression requires `SelfContained=true`; the SDK will hard-error `NETSDK1176` otherwise.

## Optional: ARM64 build (Windows on ARM)

```powershell
dotnet publish Modinator.csproj -c Release -r win-arm64 -p:SelfContained=false -p:PublishSingleFile=true -p:PlatformTarget=ARM64
```

Output: `bin\Release\net8.0-windows\win-arm64\publish\GrandeuReforged.exe`. End-users on ARM64 need the **.NET 8 Desktop Runtime ARM64** installed.

## Running the program

1. Launch *Dungeon Defenders 1* first.
2. Run `GrandeuReforged.exe`. Administrator privileges may be required so it can open a handle to the game process.
3. Pick a tool from the left sidebar.

## Why "Reforged"?

It's a modern version of the original *Grandeu*. Same idea, rebuilt from scratch with a new UI and codebase.

## Project layout

```
Modinator.csproj      Project file (target: net8.0-windows, platform: x86, WPF)
App.xaml(.cs)         WPF entry point
AssemblyInfo.cs       Standard assembly metadata
MainWindow.xaml(.cs)  Main window + sidebar + auto-kill memory loop
Models/               Plain C# — game memory layout (no WPF dependency)
  Base.cs               Static facade over Scanner (events, options, helpers)
  Scanner.cs            Memory read/write/scan
  *Native.cs            [StructLayout] structs matching DD1 in-memory layout
  *User.cs / *Search.cs Friendlier projections + per-genus search params
Views/                WPF user controls — search forms, edit forms, dialogs
Themes/               WPF resource dictionaries (colors, control styles, frameless window chrome)
Behaviors/            Small attached behaviors (numeric input, placeholder text)
Assets/               app-icon + per-stat icons used in the editor
build.ps1 / build.bat One-shot build wrappers
```

## Credits

- The original **Grandeu** author, whose decompiled WinForms tool was the structural starting point for this rewrite.
- The **DD_ModMenu** project, for documenting offsets and reverse-engineering work that informed the auto-kill and enemy-tower logic.
- The **Dungeon Defenders modding community**, for years of reverse-engineering work on UE3 struct layouts and item internals.
