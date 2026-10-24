namespace Feather;

/// <summary>Shared login detection for the native prompt and the short-lived vault module.</summary>
internal static class AutofillPageScripts
{
    // This deliberately excludes registration/reset forms and invisible fields. Both the probe
    // and the actual fill run these checks again in the top document immediately before use.
    internal const string LoginFields = """
        const visible = element => {
            if (!(element instanceof HTMLInputElement) || !element.isConnected || element.disabled || element.readOnly || element.type === 'hidden') return false;
            const rect = element.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) return false;
            for (let node = element; node instanceof Element; node = node.parentElement) {
                const style = getComputedStyle(node);
                if (style.display === 'none' || style.visibility !== 'visible' || Number(style.opacity) === 0) return false;
            }
            return true;
        };
        const tokens = element => (element.autocomplete || '').toLowerCase().split(/\s+/);
        const findLogin = () => {
            const passwords = [...document.querySelectorAll('input[type="password"]')].filter(visible);
            if (passwords.length !== 1 || passwords.some(element => tokens(element).includes('new-password'))) return null;
            const password = passwords[0];
            if (/(?:confirm|repeat|register|signup|sign-up|new[-_]?pass|create[-_]?pass)/i.test(password.id + ' ' + password.name)) return null;
            const scope = password.form || document;
            const inputs = [...scope.querySelectorAll('input')].filter(element => visible(element) && ['text', 'email', 'tel'].includes(element.type) && !tokens(element).includes('one-time-code'));
            const user = inputs.find(element => tokens(element).includes('username')) || inputs.findLast(element => (element.compareDocumentPosition(password) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0);
            if (password.form) {
                const action = new URL(password.form.action || location.href, location.href);
                if (action.origin !== location.origin) return null;
            }
            return { password, user, anchor: document.activeElement === user ? user : password };
        };
        """;

    internal static string Probe(bool focusedOnly) => "(() => { if (window !== window.top || !window.isSecureContext) return null; " + LoginFields +
        "const login = findLogin(); if (!login" + (focusedOnly ? " || ![login.password, login.user].includes(document.activeElement)" : "") +
        ") return null; const rect = login.anchor.getBoundingClientRect(); return { origin: location.origin, x: rect.x, y: rect.y, width: rect.width, height: rect.height, viewportWidth: innerWidth, viewportHeight: innerHeight }; })()";

    internal static string Observer => """
        (() => {
            if (window !== window.top || !window.isSecureContext) return;
        """ + LoginFields + """
            const seen = new WeakSet();
            let pending;
            const post = kind => {
                const login = findLogin();
                if (!login || kind === 'focus' && ![login.password, login.user].includes(document.activeElement)) {
                    if (kind === 'focus') window.chrome.webview.postMessage({ type: 'feather.credentials', kind: 'dismiss', origin: location.origin });
                    return;
                }
                if (kind === 'ready') { if (seen.has(login.password)) return; seen.add(login.password); }
                const rect = login.anchor.getBoundingClientRect();
                window.chrome.webview.postMessage({ type: 'feather.credentials', kind, origin: location.origin,
                    bounds: { x: rect.x, y: rect.y, width: rect.width, height: rect.height } });
            };
            const ready = () => { clearTimeout(pending); pending = setTimeout(() => post('ready'), 120); };
            document.addEventListener('focusin', () => queueMicrotask(() => post('focus')), true);
            document.addEventListener('focusout', () => setTimeout(() => {
                const login = findLogin();
                if (!login || ![login.password, login.user].includes(document.activeElement))
                    window.chrome.webview.postMessage({ type: 'feather.credentials', kind: 'dismiss', origin: location.origin });
            }, 0), true);
            const dismiss = () => window.chrome.webview.postMessage({ type: 'feather.credentials', kind: 'dismiss', origin: location.origin });
            document.addEventListener('scroll', dismiss, true);
            window.addEventListener('resize', dismiss);
            new MutationObserver(ready).observe(document, { childList: true, subtree: true });
            if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', ready, { once: true });
            else ready();
        })()
        """;
}
