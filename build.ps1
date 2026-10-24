$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish .\Feather.csproj -c Release -r win-x64 --self-contained false -o .\dist --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Feather publish failed.' }
    dotnet publish .\Engine\WebKitPreview\Feather.WebKit.csproj -c Release -r win-x64 --self-contained false -o .\dist\Features\WebKit --nologo
    if ($LASTEXITCODE -ne 0) { throw 'WebKit helper publish failed.' }
    Copy-Item -LiteralPath .\README.md, .\ENGINE.md -Destination .\dist
    Write-Host "Ready: $PSScriptRoot\dist\Feather.exe"
    Write-Host 'WebKit runtime is downloaded only when first requested.'
} finally {
    Pop-Location
}
