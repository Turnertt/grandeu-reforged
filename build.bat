@echo off
setlocal

rem Builds Grandeu: Reforged in Release mode and copies the final
rem GrandeuReforged.exe to .\output\GrandeuReforged.exe.
rem
rem Run from the folder containing this script:
rem     build.bat

set "SCRIPT_DIR=%~dp0"
set "CSPROJ=%SCRIPT_DIR%Modinator.csproj"
set "PUBLISH_DIR=%SCRIPT_DIR%bin\Release\net8.0-windows\win-x86\publish"
set "OUTPUT_DIR=%SCRIPT_DIR%output"
set "OUTPUT_EXE=%OUTPUT_DIR%\GrandeuReforged.exe"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet CLI not found.
    echo Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0
    exit /b 1
)

rem Stop any running copy so the build can replace the file.
taskkill /F /IM GrandeuReforged.exe >nul 2>&1

echo Building Grandeu: Reforged (Release, win-x86, single-file, framework-dependent)...

dotnet publish "%CSPROJ%" -c Release -r win-x86 -p:SelfContained=false -p:PublishSingleFile=true
if errorlevel 1 (
    echo ERROR: dotnet publish failed.
    exit /b 1
)

if not exist "%PUBLISH_DIR%\GrandeuReforged.exe" (
    echo ERROR: build succeeded but expected output not found:
    echo   %PUBLISH_DIR%\GrandeuReforged.exe
    exit /b 1
)

if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"
copy /Y "%PUBLISH_DIR%\GrandeuReforged.exe" "%OUTPUT_EXE%" >nul

echo.
echo Done. %OUTPUT_EXE%
echo End-users need the .NET 8 Desktop Runtime (x86):
echo   https://dotnet.microsoft.com/download/dotnet/8.0

endlocal
