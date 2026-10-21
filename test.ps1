$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build .\Feather.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки.' }
    $executable = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\Feather.exe'
    $core = Start-Process -FilePath $executable -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($core.ExitCode -ne 0) { throw 'Core tests failed.' }
    $smoke = Start-Process -FilePath $executable -ArgumentList '--smoke-test' -WindowStyle Hidden -Wait -PassThru
    if ($smoke.ExitCode -ne 0) { throw 'Smoke tests failed. See smoke-results.json.' }
    Get-Content -LiteralPath .\smoke-results.json -Encoding UTF8
} finally {
    Pop-Location
}
