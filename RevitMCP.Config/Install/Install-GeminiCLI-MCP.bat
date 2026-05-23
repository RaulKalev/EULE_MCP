@echo off
setlocal

:: ============================================================
:: Install-GeminiCLI-MCP.bat
:: Registers RevitMCP.Bridge.exe as an MCP server in Gemini CLI.
:: Merges the entry into %USERPROFILE%\.gemini\settings.json,
:: preserving any existing configuration in that file.
:: Run from the Install\ folder or from the repo root.
:: ============================================================

set "SCRIPT_DIR=%~dp0"
set "BRIDGE_PATH=%SCRIPT_DIR%..\..\RevitMCP.Bridge\bin\Release\net8.0\RevitMCP.Bridge.exe"

for %%i in ("%BRIDGE_PATH%") do set "BRIDGE_ABS=%%~fi"

if not exist "%BRIDGE_ABS%" (
    echo ERROR: Bridge EXE not found at: %BRIDGE_ABS%
    echo Build the solution in Release mode first:
    echo   dotnet build RevitMCP.slnx -c Release
    pause
    exit /b 1
)

set "GEMINI_DIR=%USERPROFILE%\.gemini"
set "SETTINGS_FILE=%GEMINI_DIR%\settings.json"

echo Bridge path:     %BRIDGE_ABS%
echo Settings file:   %SETTINGS_FILE%
echo.

:: Use PowerShell to safely merge the entry into settings.json.
:: ConvertFrom-Json / ConvertTo-Json handles any pre-existing content.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$bridgePath = '%BRIDGE_ABS%'.Replace('\', '\\');" ^
  "$dir = '%GEMINI_DIR%';" ^
  "$file = '%SETTINGS_FILE%';" ^
  "if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }" ^
  "if (Test-Path $file) {" ^
  "  $raw = Get-Content $file -Raw;" ^
  "  try { $settings = $raw | ConvertFrom-Json } catch { Write-Error 'settings.json is not valid JSON'; exit 1 }" ^
  "} else { $settings = [PSCustomObject]@{} }" ^
  "if (-not $settings.PSObject.Properties['mcpServers']) {" ^
  "  $settings | Add-Member -MemberType NoteProperty -Name 'mcpServers' -Value ([PSCustomObject]@{})" ^
  "}" ^
  "$entry = [PSCustomObject]@{ command = '%BRIDGE_ABS%'; args = @('--client', 'GeminiCLI') };" ^
  "$settings.mcpServers | Add-Member -MemberType NoteProperty -Name 'revit-mcp' -Value $entry -Force;" ^
  "$json = $settings | ConvertTo-Json -Depth 10;" ^
  "[System.IO.File]::WriteAllText($file, $json, (New-Object System.Text.UTF8Encoding $false));" ^
  "Write-Host 'settings.json updated.'"

if %ERRORLEVEL% neq 0 (
    echo.
    echo ERROR: Failed to update settings.json.
    pause
    exit /b 1
)

echo.
echo SUCCESS: revit-mcp registered in Gemini CLI.
echo.
echo Usage:
echo   1. Open Revit 2026 and open a model.
echo   2. Click "MCP Connector" on the RK Tools ribbon tab.
echo   3. Click "Start Connector" in the MCP window.
echo   4. Start Gemini CLI:  gemini
echo   5. Ask: call revit_get_connection_status
echo.
echo To verify the registration, check: %SETTINGS_FILE%
echo.

pause
