#Requires -Version 5.1
<#
    Builds the PORTABLE (self-contained) Grandeu: Reforged and copies it to
    .\output\GrandeuReforged-Portable.exe  (or GrandeuReforged-ARM64-Portable.exe
    with -Arm64).

    Unlike build.ps1 (framework-dependent, ~2 MB, needs the .NET 8 Desktop
    Runtime installed), this bundles the runtime into one compressed exe
    (~58 MB) that runs on machines with no .NET installed — including inside
    a Wine/Proton prefix on Linux alongside DunDefGame.exe.

    EnableCompressionInSingleFile is legal here ONLY because SelfContained=true;
    with SelfContained=false it hard-errors (NETSDK1176) — do not copy that
    flag into build.ps1.

    IncludeNativeLibrariesForSelfExtract is REQUIRED: the SDK excludes WPF's
    native DLLs (PresentationNative_cor3 etc.) from the bundle by default,
    leaving them as loose files — the lone exe then crashes at startup with
    DllNotFoundException. With the flag they're bundled and extracted to %TEMP%
    on first launch.

    Run from the folder containing this script:
        .\build-portable.ps1            # win-x86 (normal PCs)
        .\build-portable.ps1 -Arm64     # win-arm64 (Windows on ARM)
#>

param([switch]$Arm64)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Csproj    = Join-Path $ScriptDir 'Modinator.csproj'

if ($Arm64) {
    $Rid       = 'win-arm64'
    $OutName   = 'GrandeuReforged-ARM64-Portable.exe'
    $Label     = 'Release, win-arm64, single-file, SELF-CONTAINED, compressed'
} else {
    $Rid       = 'win-x86'
    $OutName   = 'GrandeuReforged-Portable.exe'
    $Label     = 'Release, win-x86, single-file, SELF-CONTAINED, compressed'
}

$PublishDir = Join-Path $ScriptDir "bin\Release\net8.0-windows\$Rid\publish-portable"
$OutputDir  = Join-Path $ScriptDir 'output'
$OutputExe  = Join-Path $OutputDir $OutName

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet CLI not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0'
}

# Stop any running copy so the build can replace the file.
Get-Process -Name 'GrandeuReforged' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Building Grandeu: Reforged ($Label)..." -ForegroundColor Cyan

$PublishArgs = @(
    'publish', $Csproj,
    '-c', 'Release',
    '-r', $Rid,
    '-p:SelfContained=true',
    '-p:PublishSingleFile=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    "-p:PublishDir=$PublishDir\"
)
if ($Arm64) { $PublishArgs += '-p:PlatformTarget=ARM64' }

& dotnet @PublishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
}

$BuiltExe = Join-Path $PublishDir 'GrandeuReforged.exe'
if (-not (Test-Path $BuiltExe)) {
    Write-Error "Build succeeded but expected output not found: $BuiltExe"
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

Copy-Item -Path $BuiltExe -Destination $OutputExe -Force

$SizeMB = [math]::Round((Get-Item $OutputExe).Length / 1MB, 1)
Write-Host ''
Write-Host "Done. $OutputExe ($SizeMB MB)" -ForegroundColor Green
Write-Host 'Self-contained: no .NET runtime install needed on the target machine.' -ForegroundColor Yellow
