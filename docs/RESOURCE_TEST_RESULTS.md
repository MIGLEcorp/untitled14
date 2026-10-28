# MBrowser memory-resource changes: local verification

Date: 2026-10-25. Device: Windows x64, project `H:\zhopa\untitled14`.

- Debug build: SUCCESS, 0 compiler warnings, 0 errors.
- Release build: SUCCESS, 0 compiler warnings, 0 errors.
- Unit/self-tests: **33 passed; 0 failed**.
- WinForms/WebView2 smoke test: **77 checks; passed** (last run 17,790 ms).
- Native DLL copied alongside both Debug and Release executables.
- DXGI query (calling process only): 0 MiB CurrentUsage and 7,365 MiB Budget
  at the self-test instant; the self-test host had not created GPU resources.
- PDH GPU Adapter Memory Dedicated Usage: 4,610 MiB across adapters at the
  self-test instant. This is **system memory usage**, not MBrowser's usage.
- End-to-end smoke check covered background WebView2 disposal, new WebView2
  creation, saved scroll restoration, and protection of a modified form.

Not measured: before/after peak browser RAM, cold/warm page-load latency on
external heavy sites, 20-tab sustained workloads, GPU frame times, long-term
memory leak rates. No percentage improvement is claimed.

To reproduce, run `resource-check.ps1` from PowerShell in the project root.
These checks use a new temporary WebView2 profile for the smoke test.
