@echo off
rem Builds the show-PC installer and the hub server package. Results: installer\Output
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "installer\build-installer.ps1" -Interactive
echo.
if exist "installer\Output" start "" "installer\Output"
pause
