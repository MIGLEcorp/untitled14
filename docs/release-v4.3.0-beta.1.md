# MBrowser 4.3.0 beta 1 — Chromium CEF integration

This **beta upgrades MBrowser 4.2.1 in place** (same installer AppId and install path).
All user data and settings under `%LOCALAPPDATA%\FeatherBrowser` are preserved.

## Changes
- Chromium Embedded Framework (CEF 154) renders pages inside the existing MBrowser window by default.
- WebView2 remains available via `MBrowser.exe --webview2` for features that have not been ported.
- Three independent opt-in compositor memory features: `SmallerInterestArea`,
  `ReclaimPrepaintTilesWhenIdle`, and `ReclaimOldPrepaintTiles`.
- Three opt-in active-page controls: offscreen image and video deferral, and
  experimental `content-visibility`.
- Memory diagnostics report aggregate Windows RAM usage of the browser and its CEF subprocesses, with CSV snapshots.
- Retains existing MBrowser UI, tabs, navigation and theme controls.
- Includes prior local resource-management and GPU diagnostics improvements.

## Known limitations
**Beta:** CEF has not yet replaced every WebView2 feature. Password autofill,
extensions, full downloads UI, translator and advanced site permissions still
require `--webview2`. Some websites may behave differently.

**No custom libcef yet:** the independently compiled Chromium/CEF with reduced
decoded-image cache is still building. This release bundles stock CEF 154 and
does not claim the custom optimization or guaranteed RAM savings.

## Installation
Download `MBrowser-4.3.0-beta.1-Setup-x64.exe` from the assets below.
Close MBrowser when prompted by the installer. Existing settings, passwords,
bookmarks, cookies and session files are not deleted. This installer uses the
**same AppId and directory** as 4.2.1, not a side-by-side installation.

`MBrowser.exe --webview2` opens the previous engine for compatibility.

See [technical details](https://github.com/MIGLEcorp/untitled14/blob/main/docs/CEF_4_3_BETA.md).
