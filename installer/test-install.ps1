param([string]$Installer = (Join-Path $PSScriptRoot '..\artifacts\Feather-3.0.0-Setup-x64.exe'))
$ErrorActionPreference = 'Stop'
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('Feather-install-' + [Guid]::NewGuid().ToString('N'))
$testApp = Join-Path $testRoot 'app'
New-Item -ItemType Directory -Path $testRoot | Out-Null
# TESTMODE disables uninstall registration, shortcuts, launch and WebView2 installation.
$process = Start-Process -FilePath $installerPath -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/TESTMODE=1',('/DIR="' + $testApp + '"'),('/LOG="' + (Join-Path $testRoot 'install.log') + '"')) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode). See $testRoot" }
foreach ($file in @('Feather.exe','Feather.dll','coreclr.dll','Modules\Feather.Features.dll','assets\rain-city.jpg','docs\menu.png')) {
    if (!(Test-Path -LiteralPath (Join-Path $testApp $file))) { throw "Missing installed file: $file" }
}
if (Test-Path -LiteralPath (Join-Path $testApp 'unins000.exe')) { throw 'Test installation unexpectedly registered an uninstaller.' }
$expected = Join-Path $PSScriptRoot '..\dist\Feather.dll'
$installedHash = (Get-FileHash -LiteralPath (Join-Path $testApp 'Feather.dll') -Algorithm SHA256).Hash
if ((Test-Path -LiteralPath $expected) -and $installedHash -ne (Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash) { throw 'Installed application does not match dist.' }
$core = Start-Process -FilePath (Join-Path $testApp 'Feather.exe') -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $testRoot 'core-results.txt') -RedirectStandardError (Join-Path $testRoot 'core-errors.txt')
if ($core.ExitCode -ne 0) { throw "Installed application tests failed. See $testRoot" }
$report = [PSCustomObject]@{ Passed=$true; InstallExitCode=$process.ExitCode; CoreExitCode=$core.ExitCode; ApplicationSha256=$installedHash; TestDirectory=$testRoot; ExistingBrowserProfileUsed=$false; Registered=$false }
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'result.json') -Encoding UTF8
Write-Output ($report | ConvertTo-Json -Compress)
