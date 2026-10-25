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
- `chrome.permissions`: optional permissions, subject to the WebView2 runtime's extension support.

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

This is the WebView2 implementation of Chromium extensions, not the complete Chrome browser UI. Chrome Store/CRX installation, browser-action toolbar popup surfaces and browser-specific UI APIs are not provided here; content scripts, storage and declarative request blocking are exercised in the real-engine smoke suite. Other APIs depend on the installed Evergreen runtime and must be tested by extension authors. Installed extensions can be disabled or removed in the internal menu.

Official references:
- https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2profile.addbrowserextensionasync
- https://developer.chrome.com/docs/extensions/reference/api
- https://developer.chrome.com/docs/extensions/reference/api/declarativeNetRequest
