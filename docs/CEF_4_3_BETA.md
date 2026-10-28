# MBrowser 4.3.0 beta 1 — Chromium CEF inside MBrowser

This is a **beta upgrade of the existing MBrowser application**, not a parallel app.
The installed version upgrades in place because `installer/MBrowser.iss` keeps
the existing `AppId`, installation directory, and Windows browser registration.

## Engines

- The default page renderer is embedded **Chromium CEF 154** via CefSharp.
- Launch `MBrowser.exe --webview2` to use the previous WebView2 renderer,
  e.g. for password manager, WebExtensions, site permissions, and translator.
- This release **does not contain custom libcef.dll** built from Chromium 154.
  Native compact decoded-image cache remains disabled until its build and
  ABI/runtime verification passes.
- Three *stock* CEF compositor features can be enabled separately:
  `SmallerInterestArea`, `ReclaimPrepaintTilesWhenIdle`,
  `ReclaimOldPrepaintTiles`. They are OFF by default and apply after restart.
- Three additional page-level experiments are opt-in: lazy offscreen images,
  deferred offscreen video, and CSS `content-visibility`; they can break pages.
- Diagnostics record the Windows private bytes, working set and total process
  count of the MBrowser + descendant CEF process tree (not per-tab RAM).
  Measurements write to `cef-memory-samples.csv` in the profile.

## User data and upgrade behavior

- Existing profile: `%LOCALAPPDATA%\FeatherBrowser` remains unchanged.
- CEF cache is placed in `%LOCALAPPDATA%\FeatherBrowser\CEFRuntime`.
- Existing WebView2 profile, DPAPI password vault, bookmarks, themes and
  session metadata are not deleted or migrated destructively.
- Original executable installation path remains
  `%LOCALAPPDATA%\Programs\MBrowser\MBrowser.exe`.
- Only one regular MBrowser instance is allowed under the historical mutex
  `Local\FeatherBrowser`; installer closes the running app with confirmation.
- No setting or Windows default-browser preference changes automatically.
- Installation does not start or replace the user's currently running browser
  until the user runs the installer and accepts closing it.

## Known beta limitations

CEF is a different renderer from WebView2. The integrated CEF navigation,
tabs, address bar, page titles, memory flags and diagnostics are implemented.
Password filling, WebExtensions, advanced permissions, translator UI, download
manager and some context-menu workflows are still WebView2-only.

For these features launch `MBrowser.exe --webview2`. RAM optimization claims
are experimental: comparison benchmarks are required before quantifying savings.

## Build and checks

Source: `H:\zhopa\untitled14`
Installer: `installer/MBrowser.iss`
Windows x64, .NET 10, CEF 154.0.34 and CefSharp 154.0.340.

```powershell
dotnet build .\MBrowser.csproj -c Release -p:Platform=x64
.\build.ps1 -Installer
.\test.ps1 -Published
```

CEF smoke test: `MBrowser.exe --cef-smoke` (isolated temporary profile).
WebView2 regression smoke: `MBrowser.exe --smoke-test` (isolated temporary profile).

Separate native CEF build in `C:\src\cef154` is not bundled in this
release until it has fully built and passed binary verification.
