$ErrorActionPreference='Stop'
$root='H:\zhopa\untitled14'
Set-Location $root
$exe=Join-Path $root 'bin\x64\Debug\net10.0-windows\MBrowser.exe'
$core=Start-Process -FilePath $exe -ArgumentList '--self-test' -WorkingDirectory $root -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput 'resource-core-results.txt' -RedirectStandardError 'resource-core-errors.txt'
Write-Host "CORE_EXIT=$($core.ExitCode)"
if (Test-Path 'resource-core-results.txt') { Get-Content 'resource-core-results.txt' -Tail 5 }
if ($core.ExitCode -ne 0) { exit 10 }
$smoke=Start-Process -FilePath $exe -ArgumentList '--smoke-test' -WorkingDirectory $root -WindowStyle Hidden -Wait -PassThru
Write-Host "SMOKE_EXIT=$($smoke.ExitCode)"
if (Test-Path 'smoke-results.json') {
    $report=Get-Content 'smoke-results.json' -Raw | ConvertFrom-Json
    Write-Host "SMOKE_PASSED=$($report.passed) CHECKS=$($report.checks.Count) DURATION_MS=$($report.elapsedMs)"
    if (!$report.passed) { Write-Host "FAIL=$($report.failure)"; exit 11 }
}
if ($smoke.ExitCode -ne 0) { exit 12 }
Write-Host "RESOURCE_CHECKS_OK"
