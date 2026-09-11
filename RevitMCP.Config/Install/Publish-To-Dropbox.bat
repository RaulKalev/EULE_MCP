@echo off
setlocal enabledelayedexpansion

:: ============================================================
:: Publish-To-Dropbox.bat  [target-folder]
:: Builds the solution in Release and lays out the deployable
:: package in the target folder (the team's shared Dropbox):
::
::   <target>\Addin\2026\RevitMCP.Addin.dll   (net8.0-windows)
::   <target>\Addin\2024\RevitMCP.Addin.dll   (net48)
::   <target>\Bridge\RevitMCP.Bridge.exe      (published, with deps)
::   <target>\Setup\EULE-MCP-Setup.exe        (the setup app)
::   <target>\version.txt
::
:: Team members then run Setup\EULE-MCP-Setup.exe and point it
:: at the target folder.
::
:: The target folder is NOT hardcoded to one machine/username. If no
:: argument is given, this script asks the local Dropbox client where
:: its sync root is (%LOCALAPPDATA%\Dropbox\info.json — works for both
:: personal Dropbox and Dropbox Business/Team folders, whatever the
:: Windows username or Dropbox folder name is on that machine) and
:: appends the fixed team-relative path below. Override by passing an
:: explicit folder as the first argument.
::
:: NOTE: publish while nobody has Revit open with the addin
:: loaded — Revit locks the DLLs and Dropbox cannot sync them.
:: ============================================================

set "SCRIPT_DIR=%~dp0"
set "REPO_ROOT=%SCRIPT_DIR%..\.."
for %%i in ("%REPO_ROOT%") do set "REPO_ABS=%%~fi"

:: Path of the deployed package folder relative to the Dropbox sync root.
:: Update this if the team folder is ever moved or renamed.
set "TEAM_SUBPATH=0_EULE  Team folder (kogu kollektiiv)\02_EULE REVIT TEMPLATE\099-scriptid\Pluginad\EULE_MCP"

set "TARGET=%~1"

if "%TARGET%"=="" (
    for /f "usebackq delims=" %%p in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$i = Join-Path $env:LOCALAPPDATA 'Dropbox\info.json'; if (Test-Path $i) { try { $j = Get-Content $i -Raw | ConvertFrom-Json; $r = if ($j.business.root_path) { $j.business.root_path } elseif ($j.business.path) { $j.business.path } elseif ($j.personal.path) { $j.personal.path } else { $null }; if ($r) { Write-Output $r } } catch {} }"`) do set "DROPBOX_ROOT=%%p"

    if not "!DROPBOX_ROOT!"=="" set "TARGET=!DROPBOX_ROOT!\%TEAM_SUBPATH%"
)

if "%TARGET%"=="" (
    echo Could not auto-detect the Dropbox folder on this machine ^(Dropbox not installed or not signed in^).
    set /p "TARGET=Target package folder (e.g. C:\Users\you\Dropbox\EULE-MCP): "
)
if "%TARGET%"=="" (
    echo ERROR: no target folder given.
    exit /b 1
)

echo Repo:   %REPO_ABS%
echo Target: %TARGET%
echo.

echo === Building solution (Release) ===
dotnet build "%REPO_ABS%\RevitMCP.slnx" -c Release
if %ERRORLEVEL% neq 0 (
    echo ERROR: build failed.
    exit /b 1
)

echo === Publishing bridge ===
dotnet publish "%REPO_ABS%\RevitMCP.Bridge\RevitMCP.Bridge.csproj" -c Release -o "%TARGET%\Bridge"
if %ERRORLEVEL% neq 0 (
    echo ERROR: bridge publish failed.
    exit /b 1
)

echo === Publishing setup app ===
dotnet publish "%REPO_ABS%\RevitMCP.Setup\RevitMCP.Setup.csproj" -c Release -o "%TARGET%\Setup"
if %ERRORLEVEL% neq 0 (
    echo ERROR: setup publish failed.
    exit /b 1
)

echo === Copying addin DLLs ===
if not exist "%TARGET%\Addin\2026" mkdir "%TARGET%\Addin\2026"
if not exist "%TARGET%\Addin\2024" mkdir "%TARGET%\Addin\2024"
copy /y "%REPO_ABS%\RevitMCP.Addin\bin\Release\net8.0-windows\RevitMCP.Addin.dll" "%TARGET%\Addin\2026\" >nul
if %ERRORLEVEL% neq 0 (
    echo ERROR: could not copy the Revit 2026 addin DLL. Is Revit running and locking it?
    exit /b 1
)
copy /y "%REPO_ABS%\RevitMCP.Addin\bin\Release\net48\RevitMCP.Addin.dll" "%TARGET%\Addin\2024\" >nul
if %ERRORLEVEL% neq 0 (
    echo ERROR: could not copy the Revit 2024 addin DLL. Is Revit running and locking it?
    exit /b 1
)

echo === Ensuring the village model folder ===
:: One shared folder for both Revit versions. Drop .glb files in here and the village renders them
:: instead of the drawn sprites; anything missing falls back, so it is safe to leave empty.
for %%K in (landmarks warehouses scenery characters) do (
    if not exist "%TARGET%\Village\Models\%%K" mkdir "%TARGET%\Village\Models\%%K"
)
if not exist "%TARGET%\Village\Models\README.txt" (
    > "%TARGET%\Village\Models\README.txt" echo Project Village 3D models. Drop glTF binaries ^(.glb^) into the sub-folders.
    >> "%TARGET%\Village\Models\README.txt" echo.
    >> "%TARGET%\Village\Models\README.txt" echo   landmarks\^<landmark id^>.glb        e.g. town_hall.glb, archive.glb, park.glb
    >> "%TARGET%\Village\Models\README.txt" echo   warehouses\^<category slug^>.glb     e.g. fire_alarm_devices.glb, _default.glb
    >> "%TARGET%\Village\Models\README.txt" echo   scenery\tree.glb, rock.glb, lamp.glb
    >> "%TARGET%\Village\Models\README.txt" echo   characters\agent.glb
    >> "%TARGET%\Village\Models\README.txt" echo.
    >> "%TARGET%\Village\Models\README.txt" echo Export: glTF 2.0 binary, +Y up, -Z front, origin at the footprint centre on y=0,
    >> "%TARGET%\Village\Models\README.txt" echo 1 unit = 1 village tile, no cameras or lights. See docs/project-village.md.
    >> "%TARGET%\Village\Models\README.txt" echo.
    >> "%TARGET%\Village\Models\README.txt" echo Click a landmark or warehouse in the viewer to see the exact file name it wants.
)

echo === Writing version.txt ===
for /f "usebackq delims=" %%h in (`git -C "%REPO_ABS%" rev-parse --short HEAD 2^>nul`) do set "GITHASH=%%h"
if "%GITHASH%"=="" set "GITHASH=unknown"
> "%TARGET%\version.txt" echo %DATE% %TIME% (%GITHASH%)

echo.
echo SUCCESS: package published to %TARGET%
echo Team members run: %TARGET%\Setup\EULE-MCP-Setup.exe
echo.
pause
