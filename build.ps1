param([switch]$Installer, [switch]$CustomCef154, [switch]$BytePatchAsyncGuard,
    [string]$NativeCefDirectory = 'C:\src\cef154\chromium\src\out\Release_GN_x64')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $target = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist'))
    if ($target -ne ([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\dist')) { throw 'Unexpected output path.' }
    # Validate the native build before touching the last-known-good staged output.
    $nativeLibPreflight = Join-Path $NativeCefDirectory 'libcef.dll'
    if (-not (Test-Path -LiteralPath $nativeLibPreflight) -or
        (Get-Item -LiteralPath $nativeLibPreflight).Length -lt 50MB) {
        throw 'Custom CEF 154 build is missing or incomplete; previous dist was preserved.'
    }
    $live = @(Get-CimInstance Win32_Process -Filter "name='MBrowser.exe'" |
        Where-Object { $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath).Equals(
                (Join-Path $target 'MBrowser.exe'), [StringComparison]::OrdinalIgnoreCase) })
    if ($live.Count -gt 0) {
        throw 'A browser is running from dist. Close only that test instance before staging; installed MBrowser was not affected.'
    }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    dotnet publish .\MBrowser.csproj -c Release -r win-x64 --self-contained true -o .\dist --nologo
    if ($LASTEXITCODE -ne 0) { throw 'MBrowser publish failed.' }
    dotnet publish .\Engine\WebKitPreview\MBrowser.WebKit.csproj -c Release -r win-x64 --self-contained true -o .\dist\Features\WebKit --nologo
    if ($LASTEXITCODE -ne 0) { throw 'WebKit helper publish failed.' }
    dotnet publish .\Engine\ImageOcr\MBrowser.Ocr.csproj -c Release -r win-x64 --self-contained true -o .\dist\Features\ImageOcr --nologo
    if ($LASTEXITCODE -ne 0) { throw 'OCR helper publish failed.' }
    Copy-Item -LiteralPath .\README.md, .\ENGINE.md -Destination .\dist
    Copy-Item -LiteralPath .\extensions -Destination .\dist\extensions -Recurse
    Copy-Item -LiteralPath .\docs -Destination .\dist\docs -Recurse
    if ($true) {
        $nativeLib = Join-Path $NativeCefDirectory 'libcef.dll'
        if (-not (Test-Path -LiteralPath $nativeLib)) { throw "Custom libcef.dll not found: $nativeLib" }
        if ((Get-Item $nativeLib).Length -lt 50MB) { throw 'Custom libcef.dll unexpectedly small' }
        # The CEF binary MUST ship with its matching V8 snapshot, resource packs,
        # GPU libraries and locales. Replacing libcef.dll alone breaks V8 contexts.
        $runtimeFiles = @(
            'libcef.dll','chrome_elf.dll','icudtl.dat','resources.pak',
            'v8_context_snapshot.bin','snapshot_blob.bin',
            'chrome_100_percent.pak','chrome_200_percent.pak',
            'vk_swiftshader.dll','vk_swiftshader_icd.json','vulkan-1.dll',
            'dxcompiler.dll','dxil.dll','d3dcompiler_47.dll',
            'libEGL.dll','libGLESv2.dll'
        )
        foreach ($name in $runtimeFiles) {
            $from = Join-Path $NativeCefDirectory $name
            if (-not (Test-Path -LiteralPath $from)) { throw "Missing custom CEF runtime file: $name" }
            Copy-Item -LiteralPath $from -Destination (Join-Path $target $name) -Force
        }
        $nativeLocales = Join-Path $NativeCefDirectory 'locales'
        if (-not (Test-Path $nativeLocales)) { throw 'Missing native CEF locales directory' }
        Get-ChildItem $nativeLocales -File -Filter '*.pak' |
            Copy-Item -Destination (Join-Path $target 'locales') -Force
        foreach ($name in @('libcef.dll','resources.pak','v8_context_snapshot.bin')) {
            $sourceHash = (Get-FileHash (Join-Path $NativeCefDirectory $name) -Algorithm SHA256).Hash
            $targetHash = (Get-FileHash (Join-Path $target $name) -Algorithm SHA256).Hash
            if ($sourceHash -ne $targetHash) { throw "Custom CEF runtime hash mismatch: $name" }
        }
        Set-Content -LiteralPath (Join-Path $target 'MBrowser.CustomCef154.marker') -NoNewline -Encoding ASCII -Value 'MBROWSER_CUSTOM_CEF154_COMPACT_CACHE_V1'
        if ($BytePatchAsyncGuard) {
            # Hardened, exact-SHA, one-byte workaround for the Chromium 154
            # base/win/post_async_results.h:332 mismatch DCHECK. This does NOT
            # repair the mismatched COM identity and must stay pre-release.
            & python (Join-Path $PSScriptRoot 'tools\patch_cef_async_guard.py') $nativeLib --target (Join-Path $target 'libcef.dll')
            if ($LASTEXITCODE -ne 0) { throw 'The guarded native Chromium patch failed; installer build aborted.' }
            Write-Host 'Experimental Chromium WinRT DCHECK byte patch staged.'
        }
        (Get-FileHash (Join-Path $target 'libcef.dll') -Algorithm SHA256).Hash |
            Set-Content -LiteralPath (Join-Path $target 'CUSTOM_LIBCEF_SHA256.txt') -Encoding ASCII
        Write-Host 'Custom CEF 154 and matching runtime resources are staged.'
    }
    if ($Installer) {
        $compiler = Join-Path $PSScriptRoot 'bin\tools\InnoSetup\ISCC.exe'
        if (!(Test-Path -LiteralPath $compiler)) {
            $installedCompiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
            if (!$installedCompiler) { throw 'Install Inno Setup 6+ or place it in bin\tools\InnoSetup.' }
            $compiler = $installedCompiler.Source
        }
        & $compiler .\installer\MBrowser.iss
        if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
    }
    Write-Host "Ready: $PSScriptRoot\dist\MBrowser.exe"
} finally { Pop-Location }
