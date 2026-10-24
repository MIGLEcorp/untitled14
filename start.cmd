@echo off
cd /d "%~dp0"
if exist "dist\Feather.exe" (
  start "" "dist\Feather.exe"
) else if exist "bin\Release\net10.0-windows\Feather.exe" (
  start "" "bin\Release\net10.0-windows\Feather.exe"
) else (
  echo Build Feather first: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
  exit /b 1
)
