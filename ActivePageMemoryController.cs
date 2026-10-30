using System.Text.Json;

namespace MBrowser;

/// <summary>
/// Conservative, opt-in active-page controls executed by Chromium CEF.
/// These are webpage-level policies, NOT replacements for Chromium's native decoded-image cache
/// or a DOM-to-disk swap mechanism.
/// </summary>
internal static class ActivePageMemoryController
{
    private const string Agent = """
(() => {
  'use strict';
  const key = '__mbrowser_memory_lab_agent_v1';
  if (globalThis[key]) { globalThis[key].configure(__CONFIG_JSON__); return; }
  if (location.protocol !== 'http:' && location.protocol !== 'https:') return;
  // Do not interfere with cross-origin embedded widgets or frames.
  if (globalThis.top !== globalThis.self) return;
  let options = {};
  let observer = null;
  let scheduled = false;
  let videoObserver = null;
  let trackedVideos = new WeakSet();
  const imageMarker = 'data-mb-lazy-added';
  const videoMarker = 'data-mb-preload-added';
  const contentMarker = 'data-mb-content-visibility';
  const styleId = 'mbrowser-memory-lab-content-visibility';

  function enabled(name) {
    const blocked = Array.isArray(options.neverHosts) &&
      options.neverHosts.some(h => location.hostname === h || location.hostname.endsWith('.' + h));
    return !blocked && options[name] === true;
  }
  function restoreImage(img) {
    if (!img.hasAttribute(imageMarker)) return;
    if (img.getAttribute('loading') === 'lazy') img.removeAttribute('loading');
    img.removeAttribute(imageMarker);
  }
  function processImage(img) {
    if (!(img instanceof HTMLImageElement)) return;
    if (!enabled('images')) { restoreImage(img); return; }
    if (img.hasAttribute(imageMarker) || img.hasAttribute('loading') ||
        img.complete || img.getAttribute('fetchpriority') === 'high') return;
    // Native lazy loading keeps near-viewport images eligible for prompt loading.
    // Does not purge already-decoded image memory.
    img.setAttribute(imageMarker, '1');
    img.setAttribute('loading', 'lazy');
  }
  function restoreVideo(video) {
    if (!video.hasAttribute(videoMarker)) return;
    if (video.getAttribute('preload') === 'none') video.removeAttribute('preload');
    video.removeAttribute(videoMarker);
    if (videoObserver) videoObserver.unobserve(video);
  }
  function processVideo(video) {
    if (!(video instanceof HTMLVideoElement)) return;
    if (!enabled('videos')) { restoreVideo(video); return; }
    if (videoObserver && !trackedVideos.has(video)) {
      trackedVideos.add(video);
      videoObserver.observe(video);
      video.addEventListener('play', () => restoreVideo(video), {once: true});
    }
    // Never alter currently visible/nearby media or a site's explicit preload policy.
    const bounds = video.getBoundingClientRect();
    const nearby = bounds.bottom >= -600 && bounds.top <= innerHeight + 600;
    if (nearby || video.autoplay || !video.paused || video.readyState > 0) {
      restoreVideo(video);
      return;
    }
    if (video.hasAttribute(videoMarker) || video.hasAttribute('preload')) return;
    video.setAttribute(videoMarker, '1');
    video.setAttribute('preload', 'none');
  }
  function processContents(root) {
    if (!(root instanceof Element)) return;
    if (root.matches('img')) processImage(root);
    else if (root.matches('video')) processVideo(root);
    for (const img of root.querySelectorAll('img')) processImage(img);
    for (const video of root.querySelectorAll('video')) processVideo(video);
  }
  function updateContentVisibility() {
    // Experimental layout skipping: never removes DOM nodes or JavaScript references.
    let style = document.getElementById(styleId);
    if (!enabled('contentVisibility')) {
      if (style) style.remove();
      document.querySelectorAll('[' + contentMarker + ']').forEach(el => el.removeAttribute(contentMarker));
      return;
    }
    if (!style && document.head) {
      style = document.createElement('style');
      style.id = styleId;
      style.textContent = 'main > article[' + contentMarker + '],main > section[' + contentMarker +
          ']{content-visibility:auto;contain-intrinsic-size:auto 700px}';
      document.head.appendChild(style);
    }
    document.querySelectorAll('main > article,main > section').forEach(el => el.setAttribute(contentMarker, '1'));
  }
  function applyDocument() {
    if (!document.documentElement) return;
    processContents(document.documentElement);
    updateContentVisibility();
  }
  const pendingRoots = new Set();
  function schedule(root) {
    if (root instanceof Element) pendingRoots.add(root);
    if (scheduled) return;
    scheduled = true;
    setTimeout(() => {
      scheduled = false;
      const roots = Array.from(pendingRoots);
      pendingRoots.clear();
      for (const item of roots) if (item.isConnected) processContents(item);
      if (enabled('contentVisibility')) updateContentVisibility();
    }, 150);
  }
  const api = {
    configure(next) {
      options = next && typeof next === 'object' ? next : {};
      if (!videoObserver && typeof IntersectionObserver === 'function') {
        videoObserver = new IntersectionObserver(entries => {
          for (const entry of entries) processVideo(entry.target);
        }, {rootMargin: '600px'});
      }
      if (!enabled('images')) {
        document.querySelectorAll('img[' + imageMarker + ']').forEach(restoreImage);
      }
      if (!enabled('videos')) {
        document.querySelectorAll('video[' + videoMarker + ']').forEach(restoreVideo);
        if (videoObserver) {
          document.querySelectorAll('video').forEach(el => videoObserver.unobserve(el));
        }
        trackedVideos = new WeakSet();
      }
      if (observer) observer.disconnect();
      if (enabled('images') || enabled('videos') || enabled('contentVisibility')) {
        if (!observer) observer = new MutationObserver(records => {
          for (const record of records) {
            if (record.type === 'attributes' && record.target instanceof Element) schedule(record.target);
            for (const node of record.addedNodes) if (node instanceof Element) schedule(node);
            if (videoObserver) for (const node of record.removedNodes) {
              if (!(node instanceof Element)) continue;
              if (node.matches('video')) videoObserver.unobserve(node);
              for (const video of node.querySelectorAll('video')) videoObserver.unobserve(video);
            }
          }
        });
        observer.observe(document, {
          childList: true, subtree: true,
          attributes: true, attributeFilter: ['src', 'srcset']
        });
      }
      // Disabling must remove injected CSS immediately, not only on a future mutation.
      updateContentVisibility();
      schedule(document.documentElement);
    }
  };
  Object.defineProperty(globalThis, key, {value: api, configurable: false});
  api.configure(__CONFIG_JSON__);
})();
""";

    internal static string OptionsJson(BrowserPreferences preferences) => JsonSerializer.Serialize(new
    {
        images = preferences.LazyOffscreenImages,
        videos = preferences.DeferOffscreenVideo,
        contentVisibility = preferences.ExperimentalContentVisibility,
        neverHosts = preferences.NeverOptimizeHosts
    });

    internal static string Script(BrowserPreferences preferences) =>
        Agent.Replace("__CONFIG_JSON__", OptionsJson(preferences), StringComparison.Ordinal);

    internal static string LiveUpdate(BrowserPreferences preferences) =>
        "globalThis.__mbrowser_memory_lab_agent_v1?.configure(" + OptionsJson(preferences) + ");";

    internal static bool IsSiteExcluded(BrowserPreferences preferences, string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;
        return preferences.NeverOptimizeHosts.Any(host =>
            uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool ShouldSetActiveLowMemoryTarget(
        BrowserPreferences preferences, string? url, long availableRamBytes)
    {
        if (IsSiteExcluded(preferences, url)) return false;
        if (preferences.ActivePageLowMemoryTarget) return true;
        return preferences.AdaptiveLowMemoryOnSystemPressure &&
            availableRamBytes >= 0 &&
            availableRamBytes < (long)preferences.SystemRamPressureThresholdMiB * 1024 * 1024;
    }

    internal static bool ShouldCollectJavaScriptGarbage(bool enabled, bool pageLoading,
        bool hasProtectedActivity, bool isForeground, TimeSpan userIdle,
        DateTimeOffset lastCollection, DateTimeOffset now) =>
        enabled && isForeground && !pageLoading && !hasProtectedActivity &&
        userIdle >= TimeSpan.FromMinutes(2) && now - lastCollection >= TimeSpan.FromMinutes(5);
}
