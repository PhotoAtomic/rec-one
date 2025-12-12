const CACHE_VERSION = 'diary-app-v2';
const CACHE_NAME = `diary-app-${CACHE_VERSION}`;
const OFFLINE_FALLBACKS = ['/', 'index.html'];

importScripts('./service-worker-assets.js');

const assetUrls = (globalThis.assetsManifest?.assets ?? [])
    .map(asset => new URL(asset.url, self.location).toString())
    .concat(OFFLINE_FALLBACKS);

self.addEventListener('install', event => {
    self.skipWaiting();
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then(cache => cache.addAll(assetUrls))
    );
});

self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys().then(keys =>
            Promise.all(keys
                .filter(key => key.startsWith('diary-app-') && key !== CACHE_NAME)
                .map(key => caches.delete(key))))
            .then(() => self.clients.claim())
    );
});

self.addEventListener('fetch', event => {
    if (event.request.method !== 'GET') {
        return;
    }

    if (event.request.cache === 'only-if-cached' && event.request.mode !== 'same-origin') {
        // Chrome WebAPK bootstrap requests use only-if-cached with cross-origin mode.
        return;
    }

    const isNavigationRequest = event.request.mode === 'navigate';

    event.respondWith(
        caches.match(event.request, { ignoreSearch: true }).then(cached => {
            if (cached) {
                return cached;
            }

            return fetch(event.request).catch(error => {
                if (isNavigationRequest) {
                    return caches.match('index.html');
                }

                throw error;
            });
        })
    );
});
