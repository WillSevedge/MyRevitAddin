@echo off
rem ---------------------------------------------------------------------------
rem  Installs the Nexus add-ins for every Revit and AutoCAD / Civil 3D / Plant 3D
rem  year (2024-2027) found on this PC. Copy this whole folder to the PC first.
rem  Close Revit and AutoCAD before running it. Install the hub separately.
rem ---------------------------------------------------------------------------
title Install Nexus add-ins
setlocal EnableDelayedExpansion
set "HERE=%~dp0"
set INSTALLED=

for %%Y in (2024 2025 2026 2027) do (
  if exist "%HERE%Revit\%%Y\Nexus.addin" (
    set "HAS="
    if exist "%ProgramFiles%\Autodesk\Revit %%Y\Revit.exe" set HAS=1
    if exist "%ProgramData%\Autodesk\Revit\Addins\%%Y" set HAS=1
    if defined HAS (
      echo Revit %%Y ...
      if not exist "%APPDATA%\Autodesk\Revit\Addins\%%Y" mkdir "%APPDATA%\Autodesk\Revit\Addins\%%Y"
      robocopy "%HERE%Revit\%%Y" "%APPDATA%\Autodesk\Revit\Addins\%%Y" /e /njh /njs /nfl /ndl /np >nul
      if errorlevel 8 (echo   Could not copy: is Revit %%Y open?) else set INSTALLED=!INSTALLED! Revit-%%Y
    )
  )
)

if exist "%HERE%Nexus.bundle\PackageContents.xml" (
  set "HAS="
  for %%Y in (2024 2025 2026 2027) do if exist "%ProgramFiles%\Autodesk\AutoCAD %%Y\acad.exe" set HAS=1
  if defined HAS (
    echo AutoCAD / Civil 3D / Plant 3D ...
    robocopy "%HERE%Nexus.bundle" "%APPDATA%\Autodesk\ApplicationPlugins\Nexus.bundle" /e /njh /njs /nfl /ndl /np >nul
    if errorlevel 8 (echo   Could not copy: is AutoCAD open?) else set INSTALLED=!INSTALLED! AutoCAD-family
  )
)

echo.
if defined INSTALLED (
  echo Installed for:%INSTALLED%
  echo Start Revit or AutoCAD: the Nexus tab appears on the ribbon.
) else (
  echo No Revit or AutoCAD 2024-2027 was found on this PC, or nothing could be copied.
)
echo.
pause
