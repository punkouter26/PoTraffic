// PoTraffic service worker — Web Push (#1) and offline support (#10).
//
// Both concerns live in one file on purpose: a scope can only have one controlling
// service worker, so a separate offline worker registered at "/" would evict this one
// and silently kill push. Registered by js/pt-pwa.js at startup, and again by
// js/pt-push.js when the user opts into notifications (registration is idempotent).

const SHELL_CACHE = 'pt-shell-v1';
const DATA_CACHE = 'pt-data-v1';
const KNOWN_CACHES = [SHELL_CACHE, DATA_CACHE];

// How long to wait for the network before falling back to the last good copy.
// A commute app opened on a train platform must not sit on a white screen waiting
// for a request that is never going to complete.
const NETWORK_TIMEOUT_MS = 2500;

// ── Install / activate ──────────────────────────────────────────────────────

self.addEventListener('install', (event) => {
    // The Blazor payload is per-build and too large to pre-cache usefully, so it
    // fills in on first visit. The root document is the exception: it is the
    // navigation fallback, and a user who only ever opens /dashboard would
    // otherwise never have it cached when they need it.
    event.waitUntil((async () => {
        try {
            const cache = await caches.open(SHELL_CACHE);
            await cache.add('/');
        } catch {
            // Offline at install time — the shell caches on the first successful visit.
        }
        await self.skipWaiting();
    })());
});

self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        const names = await caches.keys();
        await Promise.all(
            names.filter((n) => n.startsWith('pt-') && !KNOWN_CACHES.includes(n))
                 .map((n) => caches.delete(n)));
        await self.clients.claim();
    })());
});

// ── Fetch strategies ────────────────────────────────────────────────────────

/** Races the network against a timer, resolving to null if the network is too slow. */
function fetchWithTimeout(request, ms) {
    return new Promise((resolve) => {
        const timer = setTimeout(() => resolve(null), ms);
        fetch(request).then(
            (response) => { clearTimeout(timer); resolve(response); },
            () => { clearTimeout(timer); resolve(null); });
    });
}

/**
 * Fresh bytes when the network answers in time, last-known copy otherwise.
 * Used for the document, the Blazor framework payload and API reads — anywhere
 * serving a stale response while online would be wrong.
 */
async function networkFirst(request, cacheName) {
    const response = await fetchWithTimeout(request, NETWORK_TIMEOUT_MS);

    if (response && response.ok) {
        const cache = await caches.open(cacheName);
        cache.put(request, response.clone());
        return response;
    }

    const cached = await caches.match(request);
    if (cached) return cached;

    // Nothing cached and nothing on the wire. For a navigation, fall back to the
    // app shell so the SPA can boot and render its own offline state.
    if (request.mode === 'navigate') {
        const shell = await caches.match('/');
        if (shell) return shell;
    }

    return response ?? Response.error();
}

/**
 * Blazor framework assets: strictly network, falling back to cache only when the
 * network genuinely fails.
 *
 * These deliberately do NOT use the timeout race the document uses. The payload moves
 * as a coherent set — a cached .wasm served next to a freshly fetched boot manifest
 * fails its integrity check and the app will not start. A slow connection must
 * therefore wait rather than half-fall-back: online means every asset is fresh,
 * offline means every asset comes from the same cached build.
 */
async function frameworkAsset(request) {
    try {
        const response = await fetch(request);
        if (response && response.ok) {
            const cache = await caches.open(SHELL_CACHE);
            cache.put(request, response.clone());
        }
        return response;
    } catch {
        const cached = await caches.match(request);
        return cached ?? Response.error();
    }
}

/**
 * Serves the cached copy immediately and refreshes it in the background.
 * Used for styles, scripts and icons, where one render with a slightly old
 * stylesheet is a far better trade than a blocking round-trip.
 */
async function staleWhileRevalidate(request, cacheName) {
    const cache = await caches.open(cacheName);
    const cached = await cache.match(request);

    const network = fetch(request).then((response) => {
        if (response && response.ok) cache.put(request, response.clone());
        return response;
    }).catch(() => null);

    return cached ?? (await network) ?? Response.error();
}

self.addEventListener('fetch', (event) => {
    const request = event.request;

    // Only GET is cacheable, and only our own origin is ours to cache.
    if (request.method !== 'GET') return;

    const url = new URL(request.url);
    if (url.origin !== self.location.origin) return;

    // Auth endpoints must always hit the network: a cached sign-in state is a
    // security problem, not a performance win.
    if (url.pathname.startsWith('/api/auth')) return;

    if (request.mode === 'navigate') {
        event.respondWith(networkFirst(request, SHELL_CACHE));
        return;
    }

    if (url.pathname.startsWith('/api/')) {
        event.respondWith(networkFirst(request, DATA_CACHE));
        return;
    }

    if (url.pathname.startsWith('/_framework/')) {
        event.respondWith(frameworkAsset(request));
        return;
    }

    if (/\.(css|js|png|svg|ico|woff2?)$/.test(url.pathname)) {
        event.respondWith(staleWhileRevalidate(request, SHELL_CACHE));
    }
});

// ── Web Push ────────────────────────────────────────────────────────────────

self.addEventListener('push', (event) => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; }
    catch { data = { body: event.data ? event.data.text() : '' }; }

    const title = data.title || 'PoTraffic';
    const options = {
        body: data.body || '',
        icon: '/icon-192.png',
        badge: '/icon-192.png',
        tag: data.kind || 'potraffic',
        data: { routeId: data.routeId || null }
    };
    event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', (event) => {
    event.notification.close();
    const routeId = event.notification.data && event.notification.data.routeId;
    const url = routeId ? `/routes/${routeId}` : '/dashboard';
    event.waitUntil(
        self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((wins) => {
            for (const w of wins) {
                if ('focus' in w) { w.navigate(url); return w.focus(); }
            }
            if (self.clients.openWindow) return self.clients.openWindow(url);
        })
    );
});
