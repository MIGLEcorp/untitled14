param([switch]$Installer)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $target = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist'))
    if ($target -ne ([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\dist')) { throw 'Unexpected output path.' }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    dotnet publish .\MBrowser.csproj -c Release -r win-x64 --self-contained true -o .\dist --nologo
    if ($LASTEXITCODE -ne 0) { throw 'MBrowser publish failed.' }
    dotnet publish .\Engine\WebKitPreview\MBrowser.WebKit.csproj -c Release -r win-x64 --self-contained true -o .\dist\Features\WebKit --nologo
    if ($LASTEXITCODE -ne 0) { throw 'WebKit helper publish failed.' }
    Copy-Item -LiteralPath .\README.md, .\ENGINE.md -Destination .\dist
    Copy-Item -LiteralPath .\extensions -Destination .\dist\extensions -Recurse
    Copy-Item -LiteralPath .\docs -Destination .\dist\docs -Recurse
    if ($Installer) {
        $compiler = Join-Path $PSScriptRoot 'bin\tools\InnoSetup\ISCC.exe'
        if (!(Test-Path -LiteralPath $compiler)) {
            $installedCompiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
            if (!$installedCompiler) { throw 'Install Inno Setup 6+ or place it in bin\tools\InnoSetup.' }
            $compiler = $installedCompiler.Source
        }
        if (!(Test-Path -LiteralPath '.\bin\tools\MicrosoftEdgeWebview2Setup.exe')) {
            Invoke-WebRequest -UseBasicParsing 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile '.\bin\tools\MicrosoftEdgeWebview2Setup.exe'
        }
        if ((Get-AuthenticodeSignature -LiteralPath '.\bin\tools\MicrosoftEdgeWebview2Setup.exe').Status -ne 'Valid') { throw 'WebView2 bootstrapper signature is invalid.' }
        & $compiler .\installer\MBrowser.iss
        if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
    }
    Write-Host "Ready: $PSScriptRoot\dist\MBrowser.exe"
} finally { Pop-Location }
