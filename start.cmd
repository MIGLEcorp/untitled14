@echo off
cd /d "%~dp0"
if exist "dist\Feather.exe" (
  start "" "dist\Feather.exe"
) else (
  dotnet run --project Feather.csproj -c Release
)
