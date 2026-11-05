@echo off
rem ---------------------------------------------------------------------------
rem  Installs Nexus on this PC from the Nexus package folder (no Visual Studio needed):
rem   1. the add-ins for every Revit and AutoCAD / Civil 3D / Plant 3D year (2024-2027) found here,
rem   2. the Nexus hub for the current Windows user (Start Menu, Settings > Apps, starts with Windows).
rem  Close Revit and AutoCAD first. No administrator rights needed.
rem ---------------------------------------------------------------------------
title Install Nexus
set "HERE=%~dp0"

tasklist /fi "imagename eq Revit.exe" | find /i "Revit.exe" >nul && set OPEN=1
tasklist /fi "imagename eq acad.exe" | find /i "acad.exe" >nul && set OPEN=1
if defined OPEN (
  echo Revit or AutoCAD is open. Save your work and close them, then run this again.
  echo ^(Windows locks the add-in files while they run.^)
  echo.
  pause
  exit /b 1
)

echo Installing the add-ins...
call "%HERE%addins\Install-Addins.cmd"

echo Installing the Nexus hub...
"%HERE%Nexus.exe" --install
if errorlevel 1 (
  echo.
  echo The hub was NOT installed. See the log in %LOCALAPPDATA%\Nexus\logs
  pause
  exit /b 1
)
echo.
echo Done. Nexus is open: its N icon is in the system tray next to the clock.
echo Start Revit or AutoCAD: the Nexus tab appears on the ribbon.
echo.
pause
