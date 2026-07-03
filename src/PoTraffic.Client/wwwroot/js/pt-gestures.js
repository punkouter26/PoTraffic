// pt-gestures.js — small touch helper used by SwipeableRouteCard.
// No framework, no deps. Subscribes via element ref; cleans up on dispose.
// Listens for horizontal swipe ≥ 96px, fire callback("left"/"right").
// Also detects long-press ≥ 600ms with < 6px movement.

export function attachSwipe(el, dotnetRef, methodName) {
    if (!el) return () => {};
    let startX = 0, startY = 0, startT = 0, startEl = null;
    let tracking = false;
    let longPressTimer = null;

    const onStart = (e) => {
        const t = e.touches ? e.touches[0] : e;
        startX = t.clientX;
        startY = t.clientY;
        startT = performance.now();
        tracking = true;
        startEl = el;
        longPressTimer = setTimeout(() => {
            if (tracking) {
                try { dotnetRef.invokeMethodAsync(methodName, "longpress"); }
                catch { /* component gone */ }
            }
        }, 600);
    };

    const onMove = (e) => {
        if (!tracking) return;
        const t = e.touches ? e.touches[0] : e;
        const dx = t.clientX - startX;
        const dy = Math.abs(t.clientY - startY);
        if (dy > 12) { tracking = false; clearTimeout(longPressTimer); return; }
        if (Math.abs(dx) > 8) e.preventDefault?.();
        clearTimeout(longPressTimer);
    };

    const onEnd = (e) => {
        if (!tracking) { clearTimeout(longPressTimer); return; }
        tracking = false;
        clearTimeout(longPressTimer);
        const t = e.changedTouches ? e.changedTouches[0] : e;
        const dx = t.clientX - startX;
        const elapsed = performance.now() - startT;
        if (elapsed > 800 && Math.abs(dx) < 6) {
            try { dotnetRef.invokeMethodAsync(methodName, "longpress"); }
            catch { /* gone */ }
            return;
        }
        if (dx <= -96) {
            try { dotnetRef.invokeMethodAsync(methodName, "left"); }
            catch { /* gone */ }
        } else if (dx >= 96) {
            try { dotnetRef.invokeMethodAsync(methodName, "right"); }
            catch { /* gone */ }
        }
    };

    el.addEventListener("touchstart", onStart, { passive: false });
    el.addEventListener("touchmove", onMove, { passive: false });
    el.addEventListener("touchend", onEnd);
    el.addEventListener("touchcancel", () => { tracking = false; clearTimeout(longPressTimer); });

    return () => {
        el.removeEventListener("touchstart", onStart);
        el.removeEventListener("touchmove", onMove);
        el.removeEventListener("touchend", onEnd);
        clearTimeout(longPressTimer);
    };
}