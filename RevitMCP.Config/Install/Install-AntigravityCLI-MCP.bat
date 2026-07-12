@echo off
setlocal

:: ============================================================
:: Install-AntigravityCLI-MCP.bat
:: Registers RevitMCP.Bridge.exe as an MCP server for Antigravity
:: (CLI, IDE, and Antigravity 2.0 share the same config).
:: Merges the entry into %USERPROFILE%\.gemini\config\mcp_config.json,
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

set "CONFIG_DIR=%USERPROFILE%\.gemini\config"
set "CONFIG_FILE=%CONFIG_DIR%\mcp_config.json"

echo Bridge path:     %BRIDGE_ABS%
echo Config file:     %CONFIG_FILE%
echo.

:: Use PowerShell to safely merge the entry into mcp_config.json.
:: ConvertFrom-Json / ConvertTo-Json handles any pre-existing content.
:: An empty file (Antigravity creates one on first run) is treated as {}.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$dir = '%CONFIG_DIR%';" ^
  "$file = '%CONFIG_FILE%';" ^
  "if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }" ^
  "$raw = if (Test-Path $file) { Get-Content $file -Raw } else { $null };" ^
  "if ([string]::IsNullOrWhiteSpace($raw)) { $settings = [PSCustomObject]@{} }" ^
  "else { try { $settings = $raw | ConvertFrom-Json } catch { Write-Error 'mcp_config.json is not valid JSON'; exit 1 } }" ^
  "if (-not $settings.PSObject.Properties['mcpServers']) {" ^
  "  $settings | Add-Member -MemberType NoteProperty -Name 'mcpServers' -Value ([PSCustomObject]@{})" ^
  "}" ^
  "$entry = [PSCustomObject]@{ command = '%BRIDGE_ABS%'; args = @('--client', 'AntigravityCLI') };" ^
  "$settings.mcpServers | Add-Member -MemberType NoteProperty -Name 'revit-mcp' -Value $entry -Force;" ^
  "$json = $settings | ConvertTo-Json -Depth 10;" ^
  "[System.IO.File]::WriteAllText($file, $json, (New-Object System.Text.UTF8Encoding $false));" ^
  "Write-Host 'mcp_config.json updated.'"

if %ERRORLEVEL% neq 0 (
    echo.
    echo ERROR: Failed to update mcp_config.json.
    pause
    exit /b 1
)

echo.
echo SUCCESS: revit-mcp registered for Antigravity.
echo.
echo Usage:
echo   1. Open Revit 2026 and open a model.
echo   2. Click "MCP Connector" on the RK Tools ribbon tab.
echo   3. Click "Start Connector" in the MCP window.
echo   4. Start Antigravity CLI:  agy
echo      ^(or use the Antigravity IDE - same config file^)
echo   5. Type /mcp to verify revit-mcp is connected, then ask:
echo      call revit_get_connection_status
echo.
echo To verify the registration, check: %CONFIG_FILE%
echo.

pause
