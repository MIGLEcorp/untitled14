param([string]$Installer = (Join-Path $PSScriptRoot '..\artifacts\MBrowser-4.4.0-rc.8-Setup-x64.exe'))
$ErrorActionPreference = 'Stop'
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('MBrowser-install-' + [Guid]::NewGuid().ToString('N'))
$testApp = Join-Path $testRoot 'app'
New-Item -ItemType Directory -Path $testRoot | Out-Null
New-Item -ItemType Directory -Path (Join-Path $testApp 'Modules') -Force | Out-Null
# Seed only disposable fixtures to exercise the rename/upgrade cleanup.
New-Item -ItemType Directory -Path (Join-Path $testApp 'Feather') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $testApp 'Feather.exe') -Value 'legacy fixture'
Set-Content -LiteralPath (Join-Path $testApp 'Modules\Feather.Features.dll') -Value 'legacy fixture'
Set-Content -LiteralPath (Join-Path $testApp 'preserved.txt') -Value 'preserve this file'
# TESTMODE disables uninstall registration, shortcuts and launch.
$process = Start-Process -FilePath $installerPath -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/TESTMODE=1',('/DIR="' + $testApp + '"'),('/LOG="' + (Join-Path $testRoot 'install.log') + '"')) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode). See $testRoot" }
foreach ($file in @('MBrowser.exe','MBrowser.dll','coreclr.dll','Modules\MBrowser.Features.dll','Features\ImageOcr\MBrowser.Ocr.exe','Features\ImageOcr\Microsoft.Windows.SDK.NET.dll','assets\sakura-home.png','assets\sakura-menu.png','assets\sakura-header.png','assets\mbrowser.ico','assets\REN-PY-ART-LICENSE.txt','docs\menu.png','docs\theme-editor.png','docs\image-translation.png')) {
    if (!(Test-Path -LiteralPath (Join-Path $testApp $file))) { throw "Missing installed file: $file" }
}
if (Test-Path -LiteralPath (Join-Path $testApp 'unins000.exe')) { throw 'Test installation unexpectedly registered an uninstaller.' }
if (!(Test-Path -LiteralPath (Join-Path $testApp '_MBrowser_TestProfileOnly'))) { throw 'Test install must be isolated from the real browser profile.' }
$migrationBlocked = Start-Process -FilePath (Join-Path $testApp 'MBrowser.exe') -ArgumentList '--migrate-profile' -WindowStyle Hidden -Wait -PassThru
if ($migrationBlocked.ExitCode -ne 5) { throw 'Test-installed browser must refuse real-profile migration.' }
if ((Test-Path -LiteralPath (Join-Path $testApp 'Feather.exe')) -or (Test-Path -LiteralPath (Join-Path $testApp 'Modules\Feather.Features.dll'))) { throw 'Legacy executable fixtures were not removed.' }
if (Test-Path -LiteralPath (Join-Path $testApp 'Feather')) { throw 'Legacy empty Feather folder was not removed.' }
if ((Get-Content -LiteralPath (Join-Path $testApp 'preserved.txt') -Raw).Trim() -ne 'preserve this file') { throw 'Installer changed an unrelated file.' }
$metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $testApp 'MBrowser.exe'))
if ($metadata.ProductName -ne 'MBrowser' -or $metadata.FileDescription -ne 'MBrowser') { throw 'MBrowser process branding is missing.' }
$expected = Join-Path $PSScriptRoot '..\dist\MBrowser.dll'
$installedHash = (Get-FileHash -LiteralPath (Join-Path $testApp 'MBrowser.dll') -Algorithm SHA256).Hash
if ((Test-Path -LiteralPath $expected) -and $installedHash -ne (Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash) { throw 'Installed application does not match dist.' }
$core = Start-Process -FilePath (Join-Path $testApp 'MBrowser.exe') -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $testRoot 'core-results.txt') -RedirectStandardError (Join-Path $testRoot 'core-errors.txt')
if ($core.ExitCode -ne 0) { throw "Installed application tests failed. See $testRoot" }
$languages = Join-Path $testRoot 'ocr-languages.json'
$ocr = Start-Process -FilePath (Join-Path $testApp 'Features\ImageOcr\MBrowser.Ocr.exe') -ArgumentList @('--languages',('"' + $languages + '"')) -WindowStyle Hidden -Wait -PassThru
if ($ocr.ExitCode -ne 0 -or !(Test-Path -LiteralPath $languages)) { throw "Installed OCR module failed. See $testRoot" }
$report = [PSCustomObject]@{ Passed=$true; InstallExitCode=$process.ExitCode; CoreExitCode=$core.ExitCode; OcrExitCode=$ocr.ExitCode; ApplicationSha256=$installedHash; TestDirectory=$testRoot; ExistingBrowserProfileUsed=$false; Registered=$false }
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'result.json') -Encoding UTF8
Write-Output ($report | ConvertTo-Json -Compress)
