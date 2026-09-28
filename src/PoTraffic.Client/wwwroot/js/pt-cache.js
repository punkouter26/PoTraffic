// pt-cache.js — bulk client-storage maintenance for ClientCache.
//
// Single-key get/set go through the built-in localStorage interop directly;
// only the sweep needs a loop, which is what this module is for.

/**
 * Wipes the localStorage snapshots ClientCache writes for every user. Called on
 * sign-out — a shared browser must not let the next account page through the
 * previous one's routes. The service worker never caches /api, so there is no
 * second copy to clear.
 */
export function clearUserData(prefix) {
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
