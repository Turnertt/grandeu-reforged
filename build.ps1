#Requires -Version 5.1
<#
    Builds Grandeu: Reforged in Release mode and copies the final
    GrandeuReforged.exe to .\output\GrandeuReforged.exe.

    Run from the folder containing this script:
        .\build.ps1
#>

$ErrorActionPreference = 'Stop'

$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$Csproj      = Join-Path $ScriptDir 'Modinator.csproj'
$PublishDir  = Join-Path $ScriptDir 'bin\Release\net8.0-windows\win-x86\publish'
$OutputDir   = Join-Path $ScriptDir 'output'
$OutputExe   = Join-Path $OutputDir 'GrandeuReforged.exe'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet CLI not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0'
}

# Stop any running copy so the build can replace the file.
Get-Process -Name 'GrandeuReforged' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host 'Building Grandeu: Reforged (Release, win-x86, single-file, framework-dependent)...' -ForegroundColor Cyan

& dotnet publish $Csproj `
    -c Release `
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
Write-Host ''
Write-Host "Done. $OutputExe ($SizeKB KB)" -ForegroundColor Green
Write-Host 'End-users need the .NET 8 Desktop Runtime (x86):' -ForegroundColor Yellow
Write-Host '  https://dotnet.microsoft.com/download/dotnet/8.0' -ForegroundColor Yellow
