param([switch]$Published)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if ($Published) {
        $executable = Join-Path $PSScriptRoot 'dist\Feather.exe'
    } else {
        dotnet build .\Feather.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
        $executable = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\Feather.exe'
    }
    if (!(Test-Path -LiteralPath $executable)) { throw 'Build Feather first with build.ps1.' }
    $core = Start-Process -FilePath $executable -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput '.\core-results.txt' -RedirectStandardError '.\core-errors.txt'
    Get-Content -LiteralPath .\core-results.txt -Encoding UTF8
    if ($core.ExitCode -ne 0) { throw 'Core tests failed. See core-results.txt and core-errors.txt.' }
    $smoke = Start-Process -FilePath $executable -ArgumentList '--smoke-test' -WindowStyle Hidden -Wait -PassThru
    $report = Get-Content -LiteralPath .\smoke-results.json -Encoding UTF8 -Raw | ConvertFrom-Json
    if ($smoke.ExitCode -ne 0 -or !$report.passed) { throw "Smoke tests failed: $($report.failure)" }
    Write-Host "Smoke: $($report.checks.Count) checks passed in $($report.elapsedMs) ms."
    Write-Host 'Details: smoke-results.json; native home: preview.png'
} finally {
    Pop-Location
}
