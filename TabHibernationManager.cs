namespace MBrowser;

/// <summary>
/// Best-effort page safety checks, never an attempt to serialize DOM, JavaScript heaps or credentials.
/// The existing atomic session JSON stores URL, navigation history and scroll coordinates on SSD.
/// </summary>
internal static class TabHibernationManager
{
    // Tracks user edits without storing any field content. Executed in every new document.
    internal const string ActivityTrackerScript = """
        (() => {
          let dirty = false;
          document.addEventListener('input', () => { dirty = true; }, true);
          document.addEventListener('change', () => { dirty = true; }, true);
          Object.defineProperty(window, '__mbrowserFormEdited', {
            configurable: false, get: () => dirty
          });
        })();
        """;

    internal const string SafeToHibernateScript = """
        (() => {
          try {
            if (window.__mbrowserFormEdited !== false) return false;
            if (document.querySelector('video,audio,[contenteditable],iframe[allow*="camera"],iframe[allow*="microphone"]'))
              return false;
            for (const el of document.querySelectorAll('input,textarea,select')) {
              if (el.disabled) continue;
              if (el.matches('input[type="password"],input[type="file"]')) return false;
              if (el.matches('input[type="checkbox"],input[type="radio"]') && el.checked !== el.defaultChecked) return false;
              if (el instanceof HTMLTextAreaElement && el.value !== el.defaultValue) return false;
              if (el instanceof HTMLInputElement &&
                  !['hidden','button','submit','reset'].includes(el.type) &&
                  el.value !== el.defaultValue) return false;
              if (el instanceof HTMLSelectElement && [...el.options].some(opt => opt.selected !== opt.defaultSelected))
                return false;
            }
            return true;
          } catch (_) { return false; }
        })()
        """;
}
