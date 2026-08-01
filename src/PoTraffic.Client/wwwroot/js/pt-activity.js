// pt-activity.js — reports page visibility and connectivity to .NET.
//
// Drives PollingComponentBase: a backgrounded tab stops polling entirely and
// refreshes the instant it comes back, instead of burning a request per route
// per minute against a tab nobody is looking at.

/**
 * Subscribes to visibility/online/offline changes.
 * Invokes `<method>(state)` on `dotnetRef` with one of:
 *   "visible" | "hidden" | "online" | "offline"
 * Returns a cleanup function reference for disposal from .NET.
 */
export function watch(dotnetRef, method) {
    const notify = (state) => {
        try { dotnetRef.invokeMethodAsync(method, state); } catch { /* component gone */ }
    };

    const onVisibility = () => notify(document.hidden ? "hidden" : "visible");
    const onOnline = () => notify("online");
    const onOffline = () => notify("offline");

    document.addEventListener("visibilitychange", onVisibility);
    window.addEventListener("online", onOnline);
    window.addEventListener("offline", onOffline);

    return () => {
        document.removeEventListener("visibilitychange", onVisibility);
        window.removeEventListener("online", onOnline);
        window.removeEventListener("offline", onOffline);
    };
}

/** Current state at subscription time, so .NET starts from truth rather than an assumption. */
export function isHidden() {
    return document.hidden === true;
}

/** False while the browser reports no connectivity. */
export function isOnline() {
    return navigator.onLine !== false;
}
