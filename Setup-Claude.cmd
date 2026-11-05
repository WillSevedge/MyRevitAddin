@echo off
rem ---------------------------------------------------------------------------
rem  Double-click to set up everything for Claude in AutoCAD / Civil 3D / Plant 3D:
rem  gets the latest code, builds and installs the add-ins and the hub (with
rem  nexus-mcp.exe), and adds "nexus" to Claude Desktop (and Claude Code).
rem  It tells you when to restart Claude Desktop.
rem ---------------------------------------------------------------------------
title Set up Claude for AutoCAD (Nexus)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Setup-Claude.ps1"
echo.
pause
