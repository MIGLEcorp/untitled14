$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish .\Feather.csproj -c Release -r win-x64 --self-contained false -o .\dist
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать Feather.' }
    Write-Host "Готово: $PSScriptRoot\dist\Feather.exe"
} finally {
    Pop-Location
}
