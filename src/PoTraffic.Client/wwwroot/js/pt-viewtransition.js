// pt-viewtransition.js — View Transitions for a Blazor WASM SPA.
//
// The native cross-document transition needs two documents. Blazor WASM never
// navigates the document: the Router swaps the routed component and the URL
// changes underneath a page that was never unloaded. So the transition has to be
// driven manually — wrap the DOM change in document.startViewTransition() and the
// browser snapshots before, snapshots after, and animates between them.
//
// The hard part is "the DOM change". Blazor's render is asynchronous and there is
// no "routing finished" event to await, so the callback resolves on the next two
// animation frames — by which point the new component's first render has been
// committed. Two frames rather than one because Blazor's renderer batches: the
// first frame carries the teardown, the second the new content.
//
// A missed transition is invisible — the page just changes the way it always did.
// That is the correct failure mode and every path below leads to it.

(function () {
    "use strict";

    if (!document.startViewTransition) return;

    /** Guards against a second navigation starting while one is mid-flight. */
    let running = false;

    function motionAllowed() {
        // The app's own setting wins; the CSS already handles the OS preference by
        // zeroing the animations. "off" skips the mechanism entirely.
        return document.documentElement.getAttribute("data-motion") !== "off";
    }

    /**
     * Resolves once Blazor has committed the new page's first render.
     * requestAnimationFrame twice, then a microtask, is the cheapest reliable
     * approximation — there is no public "render complete" signal to hook.
     */
    function nextRender() {
        return new Promise((resolve) => {
            requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
        });
    }

    /**
     * Wraps `navigate` in a view transition.
     *
     * The Blazor NavigationManager is the thing actually performing the change, so
     * this is called from .NET immediately BEFORE it navigates: the transition
     * captures the old frame, .NET navigates, and the callback waits for the new
     * frame to land.
     */
    window.ptViewTransition = {
        run: function () {
            if (running || !motionAllowed()) return;

            running = true;
            try {
                const transition = document.startViewTransition(() => nextRender());
                transition.finished
                    .catch(function () { /* superseded by another navigation */ })
                    .finally(function () { running = false; });
            } catch {
                running = false;
            }
        },
    };

    // Intercept ordinary in-app link clicks. Blazor's own link interception runs on
    // the same event; starting the transition here, in the capture phase, means the
    // "before" snapshot is taken while the old page is still on screen.
    document.addEventListener("click", function (e) {
        if (e.defaultPrevented || e.button !== 0) return;
        if (e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;

        const anchor = e.target instanceof Element ? e.target.closest("a[href]") : null;
        if (!anchor) return;

        // Same-document, same-origin, not a download, not a new tab.
        if (anchor.target && anchor.target !== "_self") return;
        if (anchor.hasAttribute("download")) return;

        let url;
        try { url = new URL(anchor.href, document.baseURI); } catch { return; }
        if (url.origin !== location.origin) return;

        // A fragment jump on the same page is not a navigation — transitioning it
        // would animate the whole page for a scroll. Settings' section index is
        // exactly this case.
        if (url.pathname === location.pathname && url.hash) return;
        if (url.href === location.href) return;

        window.ptViewTransition.run();
    }, true);
})();
