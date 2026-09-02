// service-worker.js — makes PoTraffic installable and usable without a network.
//
// STRATEGY: network-first with a cache fallback, for everything.
//
// The obvious alternative — cache-first for /_framework/* — is wrong for THIS app.
// Asset fingerprinting is deliberately off (see PoTraffic.Client.csproj), so every
// build ships new bytes under the same filenames. Cache-first would pin the browser
// to whichever build it happened to see first and hand back stale WASM after every
// deploy, with no filename change to break the tie. Network-first costs one
// conditional request per asset — which the HTTP cache answers with a 304 — and can
// never serve yesterday's app to someone who is online.
//
// The cache therefore exists for exactly one job: keeping the app usable when the
// network is not there. It is filled as a side effect of normal browsing, so an
// install always has real content behind it.

const VERSION = "v4";
const SHELL_CACHE = `potraffic-shell-${VERSION}`;
const ASSET_CACHE = `potraffic-assets-${VERSION}`;
const TILE_CACHE = `potraffic-tiles-${VERSION}`;

/** Every cache this version owns. Anything else found on activate is a previous version. */
const OWNED = [SHELL_CACHE, ASSET_CACHE, TILE_CACHE];

/**
 * Fetched at install so a cold start with no network still paints a real app rather
 * than the browser's offline page. Deliberately short: the framework's own files are
 * many, change every build, and get cached on first visit by the runtime handler.
 * An install list that goes stale is worse than one that is small.
 */
const SHELL = [
    "/",
    "/index.html",
    "/manifest.json",
    "/favicon.png",
    "/icon-192.png",
    "/css/app.css",
    "/css/pt-tokens.css",
    "/css/pt-motion.css",
    "/css/vendor.css",
    "/lib/leaflet/leaflet.css",
    "/lib/leaflet/leaflet.js",
    // The effects runtime. Small and loaded on every page — it decides whether the
    // map flow and chart draw-in may animate, so precaching it keeps a cold offline
    // start from opening on a flat page that then lights up a second later.
    "/js/pt-fx.js",
    "/js/pt-viewtransition.js",
];

/** Map tiles are immutable per {z}/{x}/{y} and large, so they get their own capped cache. */
const TILE_HOST = "tile.openstreetmap.org";
const TILE_LIMIT = 300;

self.addEventListener("install", (event) => {
    event.waitUntil((async () => {
        const cache = await caches.open(SHELL_CACHE);
        // addAll() rejects the whole install if ANY entry 404s. One renamed CSS file
        // should not leave the user with no service worker at all.
        await Promise.all(SHELL.map((url) =>
            cache.add(new Request(url, { cache: "reload" })).catch(() => { })));
        // Do NOT skipWaiting() here. A new worker that takes over mid-session can
        // serve the new build's assets to the old build's running WASM. The page asks
        // for the handover explicitly, once the user has agreed to reload.
    })());
});

self.addEventListener("activate", (event) => {
    event.waitUntil((async () => {
        const names = await caches.keys();
        await Promise.all(names
            .filter((n) => n.startsWith("potraffic-") && !OWNED.includes(n))
            .map((n) => caches.delete(n)));
        await self.clients.claim();
    })());
});

/** The page sends this after the user accepts the update prompt. */
self.addEventListener("message", (event) => {
    if (event.data === "SKIP_WAITING") self.skipWaiting();
});

self.addEventListener("fetch", (event) => {
    const { request } = event;

    if (request.method !== "GET") return;

    const url = new URL(request.url);

    if (url.hostname === TILE_HOST) {
        event.respondWith(tileFirst(request));
        return;
    }

    // Cross-origin (fonts, anything else): let the browser handle it. An opaque
    // response cannot be inspected, so a 404 would be cached as if it were content,
    // and it counts against quota at a padded size rather than its real one. Tiles
    // are the one place that trade is worth making — hence the capped cache above.
    if (url.origin !== self.location.origin) return;

    // The API is never cached here. Data freshness is the app's own decision and it
    // already keeps a snapshot in localStorage; a second, invisible copy in the
    // service worker would let a signed-out browser replay another session's data.
    if (url.pathname.startsWith("/api/") ||
        url.pathname.startsWith("/health") ||
        url.pathname.startsWith("/authentication") ||
        url.pathname.startsWith("/signin-") ||
        url.pathname.startsWith("/scalar")) return;

    if (request.mode === "navigate") {
        event.respondWith(navigateFirst(request));
        return;
    }

    event.respondWith(networkFirst(request, ASSET_CACHE));
});

/**
 * A navigation offline must land on the app shell, not on a 404. Every in-app route
 * (/dashboard, /routes/{id}) is served by the same index.html, so that one document
 * answers all of them and Blazor's router takes it from there.
 */
async function navigateFirst(request) {
    try {
        const response = await fetch(request);
        if (response.ok) {
            const cache = await caches.open(SHELL_CACHE);
            cache.put("/index.html", response.clone());
        }
        return response;
    } catch {
        const cached = await caches.match("/index.html", { cacheName: SHELL_CACHE });
        return cached ?? Response.error();
    }
}

async function networkFirst(request, cacheName) {
    try {
        const response = await fetch(request);
        // Only successful, non-partial responses are worth keeping. A 206 cannot be
        // replayed as a whole document and a 5xx would be cached as if it were content.
        if (response.ok && response.status === 200) {
            const cache = await caches.open(cacheName);
            cache.put(request, response.clone());
        }
        return response;
    } catch {
        const cached = await caches.match(request, { cacheName });
        if (cached) return cached;
        throw new Error(`Offline and not cached: ${request.url}`);
    }
}

/**
 * Tiles are cache-first: a given tile's bytes never change, and refetching them on
 * every map pan is the one place where network-first would cost real bandwidth.
 */
async function tileFirst(request) {
    const cache = await caches.open(TILE_CACHE);
    const cached = await cache.match(request);
    if (cached) return cached;

    try {
        const response = await fetch(request);

        // <img> requests are no-cors, so the tile arrives as an OPAQUE response:
        // status 0, ok false, headers unreadable. Testing `response.ok` alone —
        // the correct test everywhere else in this file — silently caches nothing
        // and leaves the map blank offline. Opaque responses replay to an <img>
        // perfectly well, which is all a tile is ever used for.
        if (response.ok || response.type === "opaque") {
            await cache.put(request, response.clone());
            trimCache(cache, TILE_LIMIT);
        }
        return response;
    } catch {
        // No tile is a grey square on the map, not a broken page.
        return Response.error();
    }
}

/** Oldest-first eviction. Cache.keys() returns insertion order, so the head is the oldest. */
async function trimCache(cache, limit) {
    const keys = await cache.keys();
    if (keys.length <= limit) return;
    await Promise.all(keys.slice(0, keys.length - limit).map((k) => cache.delete(k)));
}
