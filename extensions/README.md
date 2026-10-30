# MBrowser extension API (v1)

MBrowser enables Chromium WebExtensions, Manifest V3. Install an unpacked folder through Menu → Extensions → Install from folder. The manifest's required/optional API permissions and host/content-script matches are shown before installation. Packages are copied into the private browser profile; edits to the original source do not silently change the installed extension. Reinstall after editing.

## Entry point

Create `manifest.json` with `manifest_version: 3`, `name`, `version`. `example/` is a complete minimal extension scoped to https://example.com/.

Useful APIs for future extensions:

- `content_scripts`: isolated JavaScript/CSS injection on declared matching sites.
- `chrome.storage.local`: persistent extension settings.
- `chrome.runtime.sendMessage` / `onMessage`: messages between content scripts and the extension service worker.
- `chrome.declarativeNetRequest`: static/dynamic request blocking and redirects, suitable for a future ad blocker.
- `chrome.scripting`: scripts requested by an extension with the relevant permission, suitable for a future translator.
- `chrome.permissions`: optional permissions, compatibility depends on the CEF release and API.

The host's saved-password vault, profile paths and arbitrary OS APIs are **not** exposed to content scripts. Site permissions (camera, microphone, location, clipboard, notifications) are separate and prompted inside the window on demand.

## Request-blocking example

Add `"permissions": ["declarativeNetRequest"]`, the required `host_permissions`, and:

```json
"declarative_net_request": {
  "rule_resources": [{"id":"rules", "enabled":true, "path":"rules.json"}]
}
```

`rules.json` can contain:

```json
[{"id":1,"priority":1,"action":{"type":"block"},"condition":{"urlFilter":"||ads.example.invalid^","resourceTypes":["script","image","xmlhttprequest"]}}]
```

Request only the sites and APIs actually needed. Translation providers, credentials, lists of blocked domains and extension update distribution are left to each extension. Menu → Translator installs the official, checksum-pinned Immersive Translate package on demand. Its options and popup pages have dedicated in-window surfaces; an ad blocker is not bundled.

## Runtime limits

Extensions are loaded from unpacked folders through the Chromium extension loader
at CEF startup. Verified in an isolated CEF renderer: Manifest V3 content script,
background service worker, chrome.runtime messaging, chrome.storage.local, and
chrome-extension:// popup page. Per-extension popup/options windows are available
from the menu when the manifest declares them.

This is not the complete Chrome extension experience: installation from
Chrome Web Store and signed CRX packages, arbitrary browser-specific APIs,
and compatibility of third-party extensions have not been comprehensively tested.
Extensions need a restart after installation, enable, or disable.
They depend on bundled Chromium CEF, not Microsoft Edge Evergreen.

Official references:
- https://developer.chrome.com/docs/extensions/reference/api
- https://developer.chrome.com/docs/extensions/reference/api/declarativeNetRequest
