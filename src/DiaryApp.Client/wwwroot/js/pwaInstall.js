export async function isInstallAvailable() {
  // Already installed?
  const isStandalone = window.matchMedia && (window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true);
  if (isStandalone) return false;

  // If we already captured the prompt, it's available
  if (window.__pwaDeferredPrompt) {
    return true;
  }

  // Otherwise, attach a one-time listener and resolve when it fires
  return new Promise((resolve) => {
    const handler = (e) => {
      e.preventDefault();
      window.__pwaDeferredPrompt = e;
      resolve(true);
      window.removeEventListener('beforeinstallprompt', handler);
    };
    window.addEventListener('beforeinstallprompt', handler, { once: true });

    // Fallback timeout: if event doesn't fire soon, keep link hidden
    setTimeout(() => resolve(!!window.__pwaDeferredPrompt), 1500);
  });
}

export async function triggerInstall() {
  const promptEvent = window.__pwaDeferredPrompt;
  if (!promptEvent) return false;
  promptEvent.prompt();
  const choiceResult = await promptEvent.userChoice;
  window.__pwaDeferredPrompt = null;
  return choiceResult && choiceResult.outcome === 'accepted';
}
