@echo off
setlocal

:: ============================================================
:: Install-RevitMCP-Addin.bat
:: Creates the Revit 2026 .addin file pointing to the built DLL.
:: Run as Administrator if writing to ProgramData.
:: ============================================================

set "ADDIN_DIR=C:\ProgramData\Autodesk\Revit\Addins\2026"
set "SCRIPT_DIR=%~dp0"
set "DLL_PATH=%SCRIPT_DIR%..\..\RevitMCP.Addin\bin\Release\net8.0-windows\RevitMCP.Addin.dll"

:: Resolve to absolute path
for %%i in ("%DLL_PATH%") do set "DLL_ABS=%%~fi"

if not exist "%DLL_ABS%" (
    echo ERROR: DLL not found at: %DLL_ABS%
    echo Build the solution in Release mode first:
    echo   dotnet build RevitMCP.sln -c Release
    pause
    exit /b 1
)

if not exist "%ADDIN_DIR%" mkdir "%ADDIN_DIR%"

set "ADDIN_FILE=%ADDIN_DIR%\RevitMCP.addin"

(
echo ^<?xml version="1.0" encoding="utf-8"?^>
echo ^<RevitAddIns^>
echo   ^<AddIn Type="Application"^>
echo     ^<Name^>RevitMCP^</Name^>
echo     ^<Assembly^>%DLL_ABS%^</Assembly^>
echo     ^<AddInId^>A1B2C3D4-1111-2222-3333-444455556666^</AddInId^>
echo     ^<FullClassName^>RevitMCP.Addin.App^</FullClassName^>
echo     ^<VendorId^>RKTools^</VendorId^>
echo     ^<VendorDescription^>RK Tools^</VendorDescription^>
echo   ^</AddIn^>
echo ^</RevitAddIns^>
) > "%ADDIN_FILE%"

echo SUCCESS: .addin file written to:
echo   %ADDIN_FILE%
echo.
echo Restart Revit 2026 to load the plugin.
pause
