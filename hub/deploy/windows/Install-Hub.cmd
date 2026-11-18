@echo off
rem Installs the Show Control Hub on this PC: starts with Windows, runs in the background.
rem Double-click (it asks for administrator rights). To remove: Install-Hub.cmd /uninstall
if /i "%~1"=="/uninstall" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-hub-windows.ps1" -Uninstall
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-hub-windows.ps1"
)
