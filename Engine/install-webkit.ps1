param(
    [string]$BrowsersPath = (Join-Path $env:LOCALAPPDATA 'FeatherBrowser\Engines\WebKit')
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'WebKitPreview\MBrowser.WebKit.csproj'
$outputPath = Join-Path $PSScriptRoot 'WebKitPreview\bin\Release\net10.0-windows\win-x64\publish'
dotnet publish $projectPath -c Release -r win-x64 --self-contained false -o $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать модуль WebKit.' }
$helperPath = Join-Path $outputPath 'MBrowser.WebKit.exe'
$install = Start-Process -FilePath $helperPath -ArgumentList @('--install', '--browsers-path', ('"' + $BrowsersPath + '"')) -WindowStyle Hidden -Wait -PassThru
if ($install.ExitCode -ne 0) { throw "Установка WebKit завершилась с кодом $($install.ExitCode)." }
Write-Host "WebKit установлен в $BrowsersPath"
