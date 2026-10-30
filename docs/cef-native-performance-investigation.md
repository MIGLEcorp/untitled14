# CEF 154 slow page loads — October 30, 2026

Status: **Root cause strongly suspected, native fix not yet rebuilt.**

## Reproduced performance difference (same PC, same VPN)

Tested with a fresh temporary profile, without user cookies or extensions:

| Site | Google Chrome 156 DOMContentLoaded | MBrowser CEF 154 DOMContentLoaded |
| --- | ---: | ---: |
| accounts.google.com | 1,706 ms | 11,884 ms |
| github.com | 3,515 ms | 17,118 ms |

The CEF TTFB was 1,699 ms for Google and 952 ms for GitHub. Most of
the extra time was spent *after* receiving the main HTML response.
These tests use a headless Chrome window versus a visible CEF window
and are therefore indicative, not perfectly identical benchmarks.

## Production build incorrectly includes expensive debug checks

Current native build:
`C:\src\cef154\chromium\src\out\Release_GN_x64\args.gn`

Contains `is_debug=false`, but **does not override**
`is_official_build`, `dcheck_always_on`, or
`enable_expensive_dchecks`.

Chromium's `build/config/dcheck_always_on.gni` enables
`dcheck_always_on` for nonofficial Chromium builds and then
`enable_expensive_dchecks` as well. This is consistent with the
previous fatal `DCHECK` in this supposedly release-native DLL.
It is a strong explanation for unexpectedly slow JS/layout work.
Confirm effective GN values before claiming proof.

For a genuine optimized native release build, add to `args.gn`:

```gn
dcheck_always_on=false
enable_expensive_dchecks=false
```

Then regenerate the GN build and perform the full CEF native build.
These flags **cannot be changed at runtime** and a byte workaround
for one crashing DCHECK does not disable other expensive checks.
A complete rebuild can require ~59,000 tasks and many hours;
do not replace the installed DLL with a partially built copy.
Keep all CEF resource files and snapshots matched.

## RC9 managed-side changes, built and smoke-tested

- CEF per-resource intercepting diagnostics are no longer enabled by default.
  The RC8 handler intercepted hundreds of requests per page and scheduled
  disk writes. To opt in for investigation set
  `MBROWSER_TRACE_CEF_RESOURCES=1` before launching the browser.
- The saved-credentials observer no longer performs subtree-wide searches
  for inputs after every unrelated DOM mutation; it remains active for
  new form/input fields and focused credentials.
- Skip the large active-page memory agent when all experimental page
  memory optimizations are disabled; re-enable on demand.
- Make tab edit-activity tracking idempotent.

Validation: 41/41 core tests passed; isolated credential smoke passed,
including the main-frame message bridge. Both Chrome and CEF real-site
timings were measured **after** disabling per-resource tracing:
Google ~11.9 s and GitHub ~17.1 s, so native slowness remains.

RC9 should not be advertised as a full speed fix or stable release
without the native optimized build and verification.

Working installed MBrowser profile was not wiped or reset.
