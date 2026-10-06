// Caution! Be sure you understand the caveats before publishing an application with
// offline support. See https://aka.ms/blazor-offline-considerations

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [ /\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/ ];
const offlineAssetsExclude = [ /^service-worker\.js$/ ];

// Replace with your base path if you are hosting on a subfolder. Ensure there is a trailing '/'.
const base = "/";  // il workflow di deploy lo sostituisce con "/StradarioApp/"
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

async function onInstall(event) {
    console.info('Service worker: Install');

    // Fetch and cache all matching items from the assets manifest
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        // index.html è escluso dal controllo di integrità: il workflow di deploy ne
        // modifica il <base href> DOPO la pubblicazione, quindi il suo hash nel manifest
        // non coincide più col file servito e cache.addAll() farebbe fallire l'intera
        // installazione del service worker (niente cache, niente avvio offline).
        .map(asset => /(^|\/)index\.html$/.test(asset.url)
            ? new Request(asset.url, { cache: 'no-cache' })
            : new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
}

async function onActivate(event) {
    console.info('Service worker: Activate');

    // Delete unused caches
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
}

// Tile OSM: cache SEPARATA da quella degli asset (nome senza il prefisso
// 'offline-cache-', quindi onActivate non la cancella ai deploy), così lo sfondo
// della mappa visto l'ultima volta resta disponibile offline. Network-first: online
// si vedono sempre tile freschi (e la cache si aggiorna), offline si usa l'ultimo
// salvato. Tetto massimo di tile: i più vecchi (ordine di inserimento) vengono scartati.
const tileCacheName = 'stradario-tiles-v1';
const tileUrlPattern = /^https:\/\/[abc]\.tile\.openstreetmap\.org\//;
const tileCacheMax = 3000;

async function onTileFetch(event) {
    // a/b/c.tile... sono lo stesso tile: chiave normalizzata per non duplicarlo.
    const key = event.request.url.replace(/^https:\/\/[abc]\./, 'https://');
    const cache = await caches.open(tileCacheName);
    try {
        const response = await fetch(event.request);
        if (response.ok) {
            await cache.put(key, response.clone());
            trimTileCache(cache);
        }
        return response;
    } catch (err) {
        const cached = await cache.match(key);
        if (cached) return cached;
        throw err;
    }
}

async function trimTileCache(cache) {
    const keys = await cache.keys();
    if (keys.length <= tileCacheMax) return;
    await Promise.all(keys.slice(0, keys.length - tileCacheMax).map(k => cache.delete(k)));
}

async function onFetch(event) {
    if (event.request.method === 'GET' && tileUrlPattern.test(event.request.url)) {
        return onTileFetch(event);
    }

    let cachedResponse = null;
    if (event.request.method === 'GET') {
        // For all navigation requests, try to serve index.html from cache,
        // unless that request is for an offline resource.
        // If you need some URLs to be server-rendered, edit the following check to exclude those URLs
        const shouldServeIndexHtml = event.request.mode === 'navigate'
            && !manifestUrlList.some(url => url === event.request.url);

        const request = shouldServeIndexHtml ? 'index.html' : event.request;
        const cache = await caches.open(cacheName);
        cachedResponse = await cache.match(request);
    }

    return cachedResponse || fetch(event.request);
}
