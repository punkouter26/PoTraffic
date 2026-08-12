// pt-appearance.js — theme, density and their persistence.
//
// The <head> resolver in index.html stamps data-theme and data-density before
// first paint so the page never flashes the wrong one. This module owns the
// changes made afterwards, from Settings.
//
// Switching wraps the attribute write in a view transition where the browser
// has one. That is a safe place to use it: the app fully controls this DOM
// mutation, it is a single attribute, and the whole page legitimately
// cross-fades. Route changes deliberately do NOT get this treatment — Blazor
// owns that render, and wrapping it would mean guessing when it lands.

const THEME_KEY = "pt-theme";
const DENSITY_KEY = "pt-density";

function store(key, value) {
    try {
        if (value === null) localStorage.removeItem(key);
        else localStorage.setItem(key, value);
    } catch {
        // Private mode — the choice simply doesn't survive the session.
    }
}

function read(key) {
    try { return localStorage.getItem(key); } catch { return null; }
}

/** Applies `mutate` inside a view transition when one is available. */
function transition(mutate) {
    const reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    if (reduced || typeof document.startViewTransition !== "function") {
        mutate();
        return;
    }
    document.startViewTransition(mutate);
}

/**
 * @param {"light"|"dark"|"system"} value
 */
export function setTheme(value) {
    const root = document.documentElement;

    if (value === "system") {
        store(THEME_KEY, null);
        const dark = window.matchMedia("(prefers-color-scheme: dark)").matches;
        transition(() => root.setAttribute("data-theme", dark ? "dark" : "light"));
        return;
    }

    store(THEME_KEY, value);
    transition(() => root.setAttribute("data-theme", value));
}

/**
 * @param {"compact"|"normal"|"comfortable"} value
 */
export function setDensity(value) {
    const root = document.documentElement;

    if (value === "normal") {
        store(DENSITY_KEY, null);
        transition(() => root.removeAttribute("data-density"));
        return;
    }

    store(DENSITY_KEY, value);
    transition(() => root.setAttribute("data-density", value));
}

/** Current selections, for a settings page that has to render them as chosen. */
export function getAppearance() {
    return {
        theme: read(THEME_KEY) ?? "system",
        density: read(DENSITY_KEY) ?? "normal",
        // Reported so the UI can say the OS is already asking for these rather
        // than leaving the user wondering why the app looks different.
        prefersContrast: window.matchMedia("(prefers-contrast: more)").matches,
        prefersReducedMotion: window.matchMedia("(prefers-reduced-motion: reduce)").matches
    };
}
