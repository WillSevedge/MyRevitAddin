@echo off
rem Builds the standalone Nexus hub: one self-contained Nexus.exe in artifacts\Nexus\
rem (runs on any 64-bit Windows 10/11 PC, no .NET or Visual Studio needed).
rem   build\Publish-Hub.cmd            build only
rem   build\Publish-Hub.cmd install    build, then install on this PC and open it
rem Easiest: double-click Install-Hub.cmd in the repository folder.
setlocal
set "ROOT=%~dp0.."

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET SDK was not found. Install Visual Studio 2026 ^(or the .NET 10 SDK^) and try again.
  exit /b 1
)

rem A Nexus.exe still running from artifacts\Nexus (e.g. an install that did not finish) locks the file the
rem build has to replace. Stop it (the installed hub runs from %LOCALAPPDATA%\Nexus\Hub and is not touched),
rem then move aside anything still locked (nexus-mcp.exe in use by an AI assistant) so the build can write.
set "OUT=%ROOT%\artifacts\Nexus"
if exist "%OUT%" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "$d = [IO.Path]::GetFullPath($env:OUT); Get-Process -Name Nexus -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($d, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { Write-Host ('Stopping ' + $_.Path + ' (left running by an earlier install)'); Stop-Process -Id $_.Id -Force; $_.WaitForExit(5000) | Out-Null }; Get-ChildItem -LiteralPath $d -Filter '*.old-*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue; foreach ($n in 'Nexus.exe','nexus-mcp.exe') { $f = Join-Path $d $n; if (Test-Path -LiteralPath $f) { try { Remove-Item -LiteralPath $f -Force -ErrorAction Stop } catch { $aside = $f + '.old-' + (Get-Date -Format yyyyMMddHHmmss); Rename-Item -LiteralPath $f -NewName (Split-Path $aside -Leaf); Write-Host ($n + ' is in use; moved it aside.') } } }"
)

echo Building the Nexus hub (the first build downloads packages and takes a minute)...
rem All publish settings are on the command line (no publish profile needed).
dotnet publish "%ROOT%\src\Nexus.Hub\Nexus.Hub.csproj" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=embedded -p:AllowedReferenceRelatedFileExtensions=none ^
  -o "%ROOT%\artifacts\Nexus" -nologo -v:minimal
if errorlevel 1 (
  echo.
  echo Build failed.
  exit /b 1
)
if not exist "%ROOT%\artifacts\Nexus\Nexus.exe" (
  echo.
  echo Build finished but artifacts\Nexus\Nexus.exe is missing.
  exit /b 1
)
echo Building the MCP server for AI assistants (nexus-mcp.exe)...
dotnet publish "%ROOT%\src\Nexus.Mcp\Nexus.Mcp.csproj" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=embedded -p:AllowedReferenceRelatedFileExtensions=none ^
  -o "%ROOT%\artifacts\Nexus" -nologo -v:minimal
if errorlevel 1 (
  echo.
  echo The MCP server did not build; the hub is fine without it.
)
copy /y "%ROOT%\deploy\hub\Install.cmd" "%ROOT%\artifacts\Nexus\" >nul
echo.
echo Built: %ROOT%\artifacts\Nexus\Nexus.exe

if /i "%~1"=="install" (
  echo Installing to %LOCALAPPDATA%\Nexus\Hub ...
  "%ROOT%\artifacts\Nexus\Nexus.exe" --install
  if errorlevel 1 exit /b 1
  rem --install starts the installed hub and returns; give it a moment to come up.
  timeout /t 2 /nobreak >nul
) else (
  echo To install it: run Install.cmd in that folder, or Nexus.exe --install.
)
exit /b 0
