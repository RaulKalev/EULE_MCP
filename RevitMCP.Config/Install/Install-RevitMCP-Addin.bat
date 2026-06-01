@echo off
setlocal

:: ============================================================
:: Install-RevitMCP-Addin.bat
:: Creates .addin files for Revit 2024 (net48) and 2026 (net8).
:: Run as Administrator if writing to ProgramData.
:: ============================================================

set "SCRIPT_DIR=%~dp0"

:: --- Revit 2026 (net8.0-windows) ---
set "ADDIN_DIR_2026=C:\ProgramData\Autodesk\Revit\Addins\2026"
set "DLL_PATH_2026=%SCRIPT_DIR%..\..\RevitMCP.Addin\bin\Release\net8.0-windows\RevitMCP.Addin.dll"
for %%i in ("%DLL_PATH_2026%") do set "DLL_ABS_2026=%%~fi"

:: --- Revit 2024 (net48) ---
set "ADDIN_DIR_2024=C:\ProgramData\Autodesk\Revit\Addins\2024"
set "DLL_PATH_2024=%SCRIPT_DIR%..\..\RevitMCP.Addin\bin\Release\net48\RevitMCP.Addin.dll"
for %%i in ("%DLL_PATH_2024%") do set "DLL_ABS_2024=%%~fi"

if not exist "%DLL_ABS_2026%" (
    echo ERROR: Revit 2026 DLL not found at: %DLL_ABS_2026%
    echo Build the solution in Release mode first:
    echo   dotnet build RevitMCP.slnx -c Release
    pause
    exit /b 1
)

if not exist "%DLL_ABS_2024%" (
    echo ERROR: Revit 2024 DLL not found at: %DLL_ABS_2024%
    echo Build the solution in Release mode first:
    echo   dotnet build RevitMCP.slnx -c Release
    pause
    exit /b 1
)

:: Write Revit 2026 addin
if not exist "%ADDIN_DIR_2026%" mkdir "%ADDIN_DIR_2026%"
(
echo ^<?xml version="1.0" encoding="utf-8"?^>
echo ^<RevitAddIns^>
echo   ^<AddIn Type="Application"^>
echo     ^<Name^>RevitMCP^</Name^>
echo     ^<Assembly^>%DLL_ABS_2026%^</Assembly^>
echo     ^<AddInId^>A1B2C3D4-1111-2222-3333-444455556666^</AddInId^>
echo     ^<FullClassName^>RevitMCP.Addin.App^</FullClassName^>
echo     ^<VendorId^>RKTools^</VendorId^>
echo     ^<VendorDescription^>RK Tools^</VendorDescription^>
echo   ^</AddIn^>
echo ^</RevitAddIns^>
) > "%ADDIN_DIR_2026%\RevitMCP.addin"
echo SUCCESS: Revit 2026 .addin written to: %ADDIN_DIR_2026%\RevitMCP.addin

:: Write Revit 2024 addin
if not exist "%ADDIN_DIR_2024%" mkdir "%ADDIN_DIR_2024%"
(
echo ^<?xml version="1.0" encoding="utf-8"?^>
echo ^<RevitAddIns^>
echo   ^<AddIn Type="Application"^>
echo     ^<Name^>RevitMCP^</Name^>
echo     ^<Assembly^>%DLL_ABS_2024%^</Assembly^>
echo     ^<AddInId^>A1B2C3D4-1111-2222-3333-444455556666^</AddInId^>
echo     ^<FullClassName^>RevitMCP.Addin.App^</FullClassName^>
echo     ^<VendorId^>RKTools^</VendorId^>
echo     ^<VendorDescription^>RK Tools^</VendorDescription^>
echo   ^</AddIn^>
echo ^</RevitAddIns^>
) > "%ADDIN_DIR_2024%\RevitMCP.addin"
echo SUCCESS: Revit 2024 .addin written to: %ADDIN_DIR_2024%\RevitMCP.addin

echo.
echo Restart Revit to load the plugin.
pause
