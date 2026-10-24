// Runs in an isolated extension world, not in the browser host process.
chrome.storage.local.get({visits: 0}, state => {
  chrome.storage.local.set({visits: state.visits + 1});
  document.documentElement.dataset.featherExample = String(state.visits + 1);
});
