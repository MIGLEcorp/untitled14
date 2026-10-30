$ErrorActionPreference = 'Stop'
$env:DEPOT_TOOLS_WIN_TOOLCHAIN = '0'
$env:GYP_MSVS_OVERRIDE_PATH = 'C:\BuildTools'
$env:GYP_MSVS_VERSION = '2022'
$env:vs2022_install = 'C:\BuildTools'
$src = 'C:\src\cef154\chromium\src'
$artifacts = 'H:\zhopa\untitled14\artifacts'
$stdout = Join-Path $artifacts 'cef154-optimized-native-build.log'
$stderr = Join-Path $artifacts 'cef154-optimized-native-build-errors.log'
$status = Join-Path $artifacts 'cef154-optimized-native-build-status.txt'
$gn = Get-Content (Join-Path $src 'out\Release_GN_x64\args.gn') -Raw
if ($gn -notmatch '(?m)^dcheck_always_on=false\r?$' -or
    $gn -notmatch '(?m)^enable_expensive_dchecks=false\r?$') {
    throw 'Native debug checks were not explicitly disabled.'
}
$exe = (Get-Command ninja.exe -ErrorAction Stop).Source
Set-Content -LiteralPath $status -Value 'RUNNING: Chromium 154 libcef.dll optimized build'
Set-Location $src
$p = Start-Process -FilePath $exe -ArgumentList @(
    '-C', 'out\Release_GN_x64', '-j3', 'libcef.dll'
) -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
try { $p.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal } catch { }
Write-Output ('NATIVE_BUILD_PID=' + $p.Id)
Write-Output ('NATIVE_BUILD_LOG=' + $stdout)
while (-not $p.HasExited) {
    Start-Sleep -Seconds 20
    $p.Refresh()
    if ((Get-PSDrive C).Free -lt 25GB) {
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
        Set-Content -LiteralPath $status -Value 'STOPPED: C drive free space under 25 GB'
        throw 'Stopped native build to protect C drive free space'
    }
}
$p.Refresh()
Write-Output ('NATIVE_BUILD_EXIT=' + $p.ExitCode)
if ($p.ExitCode -ne 0) {
    Set-Content -LiteralPath $status -Value ('FAILED: exit code ' + $p.ExitCode)
    throw 'Native build failed; inspect the build error log'
}
$dll = Join-Path $src 'out\Release_GN_x64\libcef.dll'
$sha = (Get-FileHash $dll -Algorithm SHA256).Hash
Set-Content -LiteralPath $status -Value ('SUCCEEDED: SHA256=' + $sha)
Write-Output ('OPTIMIZED_NATIVE_CEF_SHA256=' + $sha)
