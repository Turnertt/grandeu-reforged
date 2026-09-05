#Requires -Version 5.1
<#
    Builds a DIAGNOSTIC (Debug) single-file GrandeuReforged.exe.

    Same shape as build.ps1 (win-x86, framework-dependent, single file) but
    compiled with DEBUG defined, which turns Base.Log back on. Release
    builds compile every log call out ([Conditional("DEBUG")]) and therefore
    produce no telemetry at all.

    Use this when something works on one machine but not another: have the
    other person run this exe, reproduce the problem, then send back BOTH

        %LOCALAPPDATA%\Modinator\modinator_log.txt
        Settings -> Diagnostics -> COPY REPORT   (pasted as text)

    Output: .\output\GrandeuReforged-DEBUG.exe  (kept separate from the
    normal release exe so the two can never be confused).

    Run from the folder containing this script:
        .\build-debug.ps1
#>

$ErrorActionPreference = 'Stop'

$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$Csproj     = Join-Path $ScriptDir 'Modinator.csproj'
$PublishDir = Join-Path $ScriptDir 'bin\Debug\net8.0-windows\win-x86\publish'
$OutputDir  = Join-Path $ScriptDir 'output'
$OutputExe  = Join-Path $OutputDir 'GrandeuReforged-DEBUG.exe'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet CLI not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0'
}

# Stop any running copy so the build can replace the file.
Get-Process -Name 'GrandeuReforged','GrandeuReforged-DEBUG' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host 'Building Grandeu: Reforged (DEBUG diagnostic, win-x86, single-file, framework-dependent)...' -ForegroundColor Cyan

# -c Debug is the whole point: it defines DEBUG, which un-mutes Base.Log.
# SelfContained=false must stay an MSBuild -p: property (see CLAUDE.md build
# gotchas); no compression here either (that needs SelfContained=true).
& dotnet publish $Csproj `
    -c Debug `
    -r win-x86 `
    -p:SelfContained=false `
    -p:PublishSingleFile=true

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

$SizeKB = [math]::Round((Get-Item $OutputExe).Length / 1KB, 1)
$LogPath = Join-Path $env:LOCALAPPDATA 'Modinator\modinator_log.txt'

Write-Host ''
Write-Host "Done. $OutputExe ($SizeKB KB)" -ForegroundColor Green
Write-Host ''
Write-Host 'This build WRITES A LOG. Ask the tester for:' -ForegroundColor Yellow
Write-Host "  1. $LogPath" -ForegroundColor Yellow
Write-Host '  2. Settings -> Diagnostics -> COPY REPORT (paste the text)' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Tell them to delete the log first, then reproduce the problem, so it only' -ForegroundColor DarkYellow
Write-Host 'contains the failing run. End-users still need the .NET 8 Desktop Runtime (x86).' -ForegroundColor DarkYellow
