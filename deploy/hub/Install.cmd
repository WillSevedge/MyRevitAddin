@echo off
rem Installs Nexus for the current Windows user (no administrator rights needed):
rem copies Nexus.exe to %LOCALAPPDATA%\Nexus\Hub, adds it to the Start Menu and Settings ^> Apps,
rem starts it with Windows, and opens it. Uninstall from Settings ^> Apps ^> Nexus.
"%~dp0Nexus.exe" --install
