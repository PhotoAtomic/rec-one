importScripts('./service-worker-assets.js');
self.addEventListener('install', (event) => {
    event.waitUntil(caches.open('diary-app-v1').then((cache) => cache.addAll(globalThis.assetsManifest.assets.map(a => a.url))));
});
self.addEventListener('fetch', (event) => {
    event.respondWith(caches.match(event.request).then((response) => response || fetch(event.request)));
});
