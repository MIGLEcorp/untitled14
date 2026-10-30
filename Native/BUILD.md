# MBrowser native DXGI helper

The source file `GpuDiagnostics.cpp` implements the **real Windows DXGI** video-memory
query interface. The produced x64 DLL exports `MB_QueryLocalVideoMemory`.

Compile on Windows with a recent mingw-w64 GCC installation:

```powershell
g++ -std=c++17 -O2 -shared -o Native\MBrowser.GpuDiagnostics.dll Native\GpuDiagnostics.cpp -ldxgi -lpdh -static-libgcc -static-libstdc++
```

The C# project copies this DLL to the application and published output. Rebuild
the DLL when changing its C++ source. If the DLL or DXGI 1.4 is unavailable,
the RAM diagnostics still work and the VRAM row reads "нет данных".

**Meaning of the numbers:** DXGI reports the **calling process's** VRAM usage
and assigned budget (not every Chromium/CEF child process). The additional PDH
export queries the Windows system-wide \\GPU Adapter Memory(*)\\Dedicated Usage
counter across visible adapters. These are different measurement scopes,
not a per-CEF-tab VRAM measurement. The DLL does not hook Chromium, change its safety settings,
move JavaScript heaps into video memory, or bypass memory protections.
