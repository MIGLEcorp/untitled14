# Chromium CEF 154 async WinRT DCHECK workaround (RC8)

Status: experimental. Not a source-level root cause fix.

RC7 repeatedly crashed at base/win/post_async_results.h:332 after Google
login navigation. WinDbg identified the conditional branch that invokes a
fatal DCHECK on two different COM async operation interface pointers.
The optimized debugger symbol mentions FaceAnalysis, but this is not evidence
that the camera was requested; template symbol attribution can be ambiguous.

The guarded patch script tools/patch_cef_async_guard.py:
- Requires exact original SHA256 D3F51E3651ECEDBE247E3F8F0FA1194F4872E74999C4B0FCC35BD40FE3BD4B8A.
- Verifies PE RVA 0x06ED82A4 and the surrounding 10 instruction bytes.
- Writes a separate copy, replacing one x64 conditional jump byte 74 with EB.
- Skips only the fatal debug assertion, leaving normal callbacks and results.

Build using: powershell -File build.ps1 -Installer -BytePatchAsyncGuard
Without -BytePatchAsyncGuard, the original native library is included.
The patched DLL SHA256 is written to CUSTOM_LIBCEF_SHA256.txt.
Do not apply the RVA to any other native build or modify a running DLL.
Reinstall RC7 to revert the native library. Keep the user's profile intact.

This patch is analogous to compiling Chromium without the DCHECK. It may
still leave a real mismatch bug. A smoke test is not a login verification.
A proper Chromium source-level correction and end-to-end tests are required.