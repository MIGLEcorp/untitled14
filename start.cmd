@echo off
cd /d "%~dp0"
if exist "dist\MBrowser.exe" (
  start "" "dist\MBrowser.exe"
) else if exist "bin\Release\net10.0-windows\MBrowser.exe" (
  start "" "bin\Release\net10.0-windows\MBrowser.exe"
) else (
  echo Build MBrowser first: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
  exit /b 1
)
