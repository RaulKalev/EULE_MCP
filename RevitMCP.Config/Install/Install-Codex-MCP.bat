@echo off
setlocal

:: ============================================================
:: Install-Codex-MCP.bat
:: Generates a Codex MCP configuration snippet for RevitMCP.Bridge.
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

:: Escape backslashes for TOML string (\ becomes \\)
set "BRIDGE_TOML=%BRIDGE_ABS:\=\\%"

set "SNIPPET_PATH=%SCRIPT_DIR%codex-mcp-snippet.toml"

(
    echo # Revit MCP connector for Codex
    echo # Add this block to: %USERPROFILE%\.codex\config.toml
    echo # Then restart Codex.
    echo.
    echo [mcp_servers.revit-mcp]
    echo command = "%BRIDGE_TOML%"
    echo args = ["--client", "Codex"]
    echo.
    echo # Optional lower-credit query-only catalog ^(31 tools instead of the full 181^):
    echo # args = ["--client", "Codex", "--tool-profile", "query"]
) > "%SNIPPET_PATH%"

echo.
echo Bridge EXE:     %BRIDGE_ABS%
echo Snippet written: %SNIPPET_PATH%
echo.
echo To enable Revit MCP in Codex:
echo   1. Open (or create): %USERPROFILE%\.codex\config.toml
echo   2. Paste the contents of codex-mcp-snippet.toml into that file
echo   3. Restart Codex
echo.
echo --- Generated snippet ---
echo.
type "%SNIPPET_PATH%"
echo.
echo -------------------------
echo.

pause
