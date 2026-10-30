param(
    [string]$ChromiumSource = 'C:\src\cef154\chromium\src',
    [ValidateRange(1, 12)][int]$Jobs = 4,
    [switch]$Installer
)
$ErrorActionPreference = 'Stop'
$patch = Join-Path $PSScriptRoot '..\patches\chromium\cef154-post-async-results-com-identity.patch'
$patch = [IO.Path]::GetFullPath($patch)
$source = [IO.Path]::GetFullPath($ChromiumSource)
if (!(Test-Path (Join-Path $source 'out\Release_GN_x64\build.ninja'))) {
    throw "CEF build directory not found under $source. Generate out\Release_GN_x64 first."
}
if (!(Test-Path $patch)) { throw "Missing Chromium source patch: $patch" }
$env:DEPOT_TOOLS_WIN_TOOLCHAIN = '0'
& git -C $source apply --reverse --check $patch 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host 'CEF WinRT callback patch is already applied.'
} else {
    & git -C $source apply --check $patch
    if ($LASTEXITCODE -ne 0) { throw 'CEF source patch does not apply cleanly; no files were changed.' }
    & git -C $source apply $patch
    if ($LASTEXITCODE -ne 0) { throw 'Failed to apply CEF source patch.' }
    Write-Host 'Applied CEF WinRT callback patch.'
}
& ninja -C (Join-Path $source 'out\Release_GN_x64') "-j$Jobs" libcef.dll
if ($LASTEXITCODE -ne 0) { throw 'Native CEF build failed.' }
$dll = Join-Path $source 'out\Release_GN_x64\libcef.dll'
if (!(Test-Path $dll) -or (Get-Item $dll).Length -lt 50MB) { throw 'Native libcef.dll is missing or incomplete.' }
Write-Host ('CEF build ready: ' + $dll)
Write-Host ('SHA256: ' + (Get-FileHash $dll -Algorithm SHA256).Hash)
if ($Installer) {
    & (Join-Path $PSScriptRoot '..\build.ps1') -CustomCef154 -Installer -NativeCefDirectory (Join-Path $source 'out\Release_GN_x64')
    if ($LASTEXITCODE -ne 0) { throw 'MBrowser installer staging failed.' }
}