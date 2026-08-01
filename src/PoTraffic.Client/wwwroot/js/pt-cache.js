// pt-cache.js — bulk client-storage maintenance for ClientCache.
//
// Single-key get/set go through the built-in localStorage interop directly;
// only the sweeps need a loop, which is what this module is for.

function clearByPrefix(prefix) {
    try {
        const doomed = [];
        for (let i = 0; i < localStorage.length; i++) {
            const key = localStorage.key(i);
            if (key && key.startsWith(prefix)) doomed.push(key);
        }
        // Collected first: removing while iterating reindexes localStorage
        // and would silently skip every second match.
        doomed.forEach((k) => localStorage.removeItem(k));
        return doomed.length;
    } catch {
        return 0;
    }
}

async function clearServiceWorkerData(cacheNames) {
    if (!('caches' in self)) return;
    try {
        await Promise.all(cacheNames.map((name) => caches.delete(name)));
    } catch {
        // Nothing to do — the app still works, it just starts cold next time.
    }
}

/**
 * Wipes every trace of the signed-in user's data from client storage: the
 * localStorage snapshots ClientCache writes, and the service worker's cached
 * API responses. Both must go on sign-out — a shared browser must not let the
 * next account page through the previous one's routes.
 */
export async function clearUserData(prefix, cacheNames) {
    const removed = clearByPrefix(prefix);
    await clearServiceWorkerData(cacheNames ?? []);
    return removed;
}
