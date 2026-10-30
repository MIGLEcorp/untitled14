# CEF 154 WinRT async callback — crash investigation and RC8 mitigation

## Evidence from the crash dumps

Three RC7 dumps were analyzed in WinDbg. The crashing frame was the
`base::win::PostAsyncResults<IVector<Windows::Media::FaceAnalysis::DetectedFace*>>`
callback in `services/shape_detection/face_detection_impl_win.cc`.
Our earlier Windows Hello / `authenticator_win.cc` attribution was incorrect.

`PostAsyncResults` captures the original `IAsyncOperation<T>` to keep it alive
and has a `DCHECK(original_async_operation.Get() == async_operation)`.
Raw interface pointer equality is not a safe COM identity comparison across
callback marshaling. The callback already uses the incoming interface pointer
for `GetResults`. The source patch replaces this invalid equality assertion
with a null pointer assertion and preserves the original reference for lifetime.

Source patch: `patches/chromium/cef154-post-async-results-com-identity.patch`.
The patch includes a typed regression test with distinct completion interface
pointers. The full C++ native rebuild and this test were **not yet completed**:
Ninja wanted roughly 59,000 build steps on this workstation.

## RC8 release bridge — narrowly scoped binary mitigation

The release bridge `tools/patch_cef_winrt_guard.py` applies a **one-byte
conditional-branch change** in exactly one known `libcef.dll` build, at
RVA `0x06ED82A4` (file offset `0x06ED78A4`).
It bypasses the offending pointer-equality DCHECK for the failing
`DetectedFace` specialization. It is *not* a recompiled source fix,
not an all-purpose Chromium stability fix, and not a repair of a COM
identity mismatch. All binaries except the pinned source SHA-256 are refused.
The source DLL remains unmodified; only `dist/libcef.dll` is patched.
The output SHA-256 must exactly match the pinned patched hash.

Run:
```powershell
.\build.ps1 -CustomCef154 -ApplyWinRTBinaryFix -Installer
```

After the full patched CEF rebuild finishes, package the native DLL without the
binary bridge:
```powershell
.\tools\build_custom_cef.ps1 -Jobs 4 -Installer
```
A native rebuild may require tens of thousands of operations and substantial
disk, CPU, and elapsed time. Do not claim its tests passed until actually run.

## Page-loading diagnostics

The browser records `cef-navigation-metrics.log`: main-frame-ready vs.
all-resources-idle elapsed time, Navigation Timing metrics, resource count,
bytes and slow resource hosts. `cef-resource-timings.log` contains slow,
failed and pending CEF requests. It stores hostnames and resource types,
not URL paths, query strings, headers, cookies or bodies.

An isolated network test uses:
```powershell
.\dist\MBrowser.exe --cef-url-smoke https://accounts.google.com/
```

An isolated WinRT FaceDetector test, with Blink's experimental
`FaceDetector` switch enabled only for that test:
```powershell
.\dist\MBrowser.exe --cef-face-detection-smoke
```

## Authentication and remaining limitations

The old `WebAuthenticationUseNativeWinApi` disable switch was removed to
restore passkey availability. The source patch targets a generic WinRT
completion helper, but **actual Windows passkey sign-in has not been verified**.
The default CEF Alloy runtime is still not full Chrome UI parity.
Do not promote RC8 to stable before real login testing, navigation benchmarks,
and a full native-source build.
