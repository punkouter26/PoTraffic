// pt-scroll.js — small DOM helpers shared across pages.
//
// Most scrolling lives in CSS (sticky headers, anchor jumps). This file exists
// for the one case a CSS solution can't cover: smooth-scroll an element with a
// known ID into view, accounting for the sticky app header's height so it
// doesn't end up hidden underneath.

/**
 * Smoothly scrolls the element with the given id into the viewport, leaving
 * the sticky app header's height as breathing room. No-op if the id is unknown.
 */
export function ptScrollIntoView(id) {
    const el = document.getElementById(id);
    if (!el) return;

    // Sticky header height — the app-shell sits at the top of the page. Reading
    // it once at call time is cheaper than maintaining another JS↔CSS contract
    // and a 64px fallback covers the exact common cases.
    const header = document.querySelector(".app-header");
    const offset = header ? Math.max(0, header.getBoundingClientRect().height - 1) : 64;

    const rect = el.getBoundingClientRect();
    const top = rect.top + window.scrollY - offset;
    window.scrollTo({ top, behavior: "smooth" });

    // Optional focus move for keyboard users, since the element being scrolled
    // to is sometimes an interactive control inside a disclosure.
    if (el.hasAttribute("tabindex")) {
        try { el.focus({ preventScroll: true }); } catch { /* older browsers */ }
    }
    else if (typeof el.focus === "function") {
        // Don't steal focus from form fields the user is editing — only move
        // it if focus would land somewhere sane.
        try { el.setAttribute("tabindex", "-1"); el.focus({ preventScroll: true }); } catch { /* ignore */ }
    }
}
