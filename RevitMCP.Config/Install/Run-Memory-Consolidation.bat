@echo off
setlocal

:: ============================================================
:: Run-Memory-Consolidation.bat
:: The unattended shared-memory consolidation pass (docs/shared-memory.md, "Phase 4 as built").
:: Runs Claude Code non-interactively against the bridge with only the memory tools; it writes
:: proposals, never entries - a lead accepts or rejects them on the review page (revit_memory_page).
:: Revit does not need to be open: memory is plain files.
::
:: Projects: pass project roots as arguments, or set memory.consolidation.projectRoots in the
:: user or company config.
::
:: Schedule it (weekly, Monday 06:00) on one workstation:
::   schtasks /Create /SC WEEKLY /D MON /ST 06:00 /TN "EULE memory consolidation" ^
::            /TR "\"<full path>\Run-Memory-Consolidation.bat\""
:: ============================================================

set "SCRIPT_DIR=%~dp0"
set "BRIDGE_PATH=%SCRIPT_DIR%..\..\RevitMCP.Bridge\bin\Release\net8.0\RevitMCP.Bridge.exe"
for %%i in ("%BRIDGE_PATH%") do set "BRIDGE_ABS=%%~fi"

if not exist "%BRIDGE_ABS%" (
    echo ERROR: Bridge EXE not found at: %BRIDGE_ABS%
    echo Build the solution in Release mode first:
    echo   dotnet build RevitMCP.slnx -c Release
    exit /b 1
)

where claude >nul 2>nul
if errorlevel 1 (
    echo ERROR: Claude Code CLI ^(claude^) is not in PATH.
    exit /b 1
)

set "LOG_DIR=%LOCALAPPDATA%\RKTools\RevitMCP\Logs"
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"
set "LOG=%LOG_DIR%\memory-consolidation.log"

:: A private MCP config: the bridge with the core tools plus the memory group, nothing else.
set "BRIDGE_JSON=%BRIDGE_ABS:\=\\%"
set "CONFIG=%TEMP%\eule-memory-consolidation.mcp.json"
> "%CONFIG%" echo {"mcpServers":{"revit-mcp":{"command":"%BRIDGE_JSON%","args":["--client","Claude Code (consolidation)","--tool-groups","memory"]}}}

set "ROOTS=%*"
set "PROMPT=Run the weekly shared-memory consolidation pass. 1) Call revit_memory_consolidate action=read"
if not "%ROOTS%"=="" set "PROMPT=%PROMPT% with projectRoots %ROOTS%"
set "PROMPT=%PROMPT%. 2) Follow its howTo: write merge, rewrite, retire and promote proposals with action=propose, only where you are confident; never change entries yourself. 3) Call action=done with the readAt from step 1. 4) Report what you proposed, per project, in a few lines."

echo ==== %DATE% %TIME% ==== >> "%LOG%"
claude -p "%PROMPT%" --mcp-config "%CONFIG%" --strict-mcp-config --allowedTools "mcp__revit-mcp__revit_memory_consolidate" >> "%LOG%" 2>&1
set "RC=%ERRORLEVEL%"
del "%CONFIG%" >nul 2>nul

if not "%RC%"=="0" (
    echo Consolidation failed with exit code %RC%. See %LOG%
    exit /b %RC%
)
echo Consolidation done. Proposals wait on the review page; log: %LOG%
exit /b 0
