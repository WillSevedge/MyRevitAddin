@echo off
rem ---------------------------------------------------------------------------
rem  Builds the Nexus add-ins for every supported program year (2024-2027):
rem  Revit, and AutoCAD / Civil 3D / Plant 3D.
rem   - Installs each year's add-in on this PC when that year is installed here.
rem   - Collects every year under artifacts\addins\ with Install-Addins.cmd, to copy
rem     to other PCs (double-click Install-Addins.cmd there; no Visual Studio needed).
rem  Close Revit and AutoCAD first, or their add-ins are not updated.
rem  The hub is the same for every year: build it with Install-Hub.cmd.
rem ---------------------------------------------------------------------------
setlocal EnableDelayedExpansion
set "ROOT=%~dp0.."
set "STAGE=%ROOT%\artifacts\addins"
set YEARS=2024 2025 2026 2027

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET SDK was not found. Install Visual Studio 2026 ^(or the .NET 10 SDK^) and try again.
  exit /b 1
)

if exist "%STAGE%" rmdir /s /q "%STAGE%"
set FAILED=

for %%Y in (%YEARS%) do (
  echo.
  echo ==== %%Y ====
  for %%P in (Nexus.Agent.Revit Nexus.Agent.Acad Nexus.Agent.Acad.Civil3D Nexus.Agent.Acad.Plant3D Nexus.Agent.Acad.Console) do (
    dotnet build "%ROOT%\src\%%P\%%P.csproj" -c Release -p:HostYear=%%Y "-p:NexusStageDir=%STAGE%" -nologo -v:minimal
    if errorlevel 1 set FAILED=!FAILED! %%P-%%Y
  )
)

copy /y "%ROOT%\deploy\Install-Addins.cmd" "%STAGE%\" >nul

echo.
if defined FAILED (
  echo Some builds failed: %FAILED%
  exit /b 1
)
echo All years built. To set up another PC, copy %STAGE% there and double-click Install-Addins.cmd.
exit /b 0
