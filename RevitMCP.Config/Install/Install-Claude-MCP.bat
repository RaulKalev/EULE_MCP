@echo off
setlocal

:: ============================================================
:: Install-Claude-MCP.bat
:: Registers RevitMCP.Bridge.exe as an MCP server in Claude Code.
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

echo Registering revit-mcp MCP server with Claude Code...
echo Bridge path: %BRIDGE_ABS%
echo.

claude mcp add --scope user revit-mcp "%BRIDGE_ABS%" -- --client "Claude Code"

if %ERRORLEVEL% == 0 (
    echo.
    echo SUCCESS: revit-mcp registered.
    echo.
    echo Usage:
    echo   1. Open Revit 2026 and open a model.
    echo   2. Click "MCP Connector" on the RK Tools ribbon tab.
    echo   3. Click "Start Connector" in the MCP window.
    echo   4. Ask Claude Code: call revit_get_connection_status
) else (
    echo.
    echo ERROR: Registration failed. Is Claude Code installed and in PATH?
    echo Install from: https://claude.ai/download
)

pause
