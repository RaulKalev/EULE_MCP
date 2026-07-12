@echo off
setlocal

:: ============================================================
:: Publish-To-Dropbox.bat  <target-folder>
:: Builds the solution in Release and lays out the deployable
:: package in the target folder (your team's shared Dropbox):
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
:: NOTE: publish while nobody has Revit open with the addin
:: loaded — Revit locks the DLLs and Dropbox cannot sync them.
:: ============================================================

set "SCRIPT_DIR=%~dp0"
set "REPO_ROOT=%SCRIPT_DIR%..\.."
for %%i in ("%REPO_ROOT%") do set "REPO_ABS=%%~fi"

set "TARGET=%~1"
if "%TARGET%"=="" (
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

echo === Writing version.txt ===
for /f "usebackq delims=" %%h in (`git -C "%REPO_ABS%" rev-parse --short HEAD 2^>nul`) do set "GITHASH=%%h"
if "%GITHASH%"=="" set "GITHASH=unknown"
> "%TARGET%\version.txt" echo %DATE% %TIME% (%GITHASH%)

echo.
echo SUCCESS: package published to %TARGET%
echo Team members run: %TARGET%\Setup\EULE-MCP-Setup.exe
echo.
pause
