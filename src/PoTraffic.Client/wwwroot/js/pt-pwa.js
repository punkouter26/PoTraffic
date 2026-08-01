// pt-pwa.js — service-worker registration and the install prompt (#10).
//
// The browser fires `beforeinstallprompt` once, early, and only when it considers
// the app installable. Miss it and the prompt is gone for the session, so it is
// captured here at module load rather than when some component gets around to asking.

let deferredPrompt = null;
let promptAvailable = false;

if (typeof window !== 'undefined') {
    window.addEventListener('beforeinstallprompt', (e) => {
        // Suppress the browser's own mini-infobar so the in-app card is the single
        // place this is offered.
        e.preventDefault();
        deferredPrompt = e;
        promptAvailable = true;
    });

    window.addEventListener('appinstalled', () => {
        deferredPrompt = null;
        promptAvailable = false;
    });
}

/**
 * Registers the service worker that backs offline support and push.
 * Safe to call repeatedly — registration for the same URL is idempotent.
 */
export async function register() {
    if (!('serviceWorker' in navigator)) return false;
    try {
        await navigator.serviceWorker.register('/push-sw.js');
        return true;
    } catch {
        // A blocked or unsupported worker costs offline support, nothing more.
        return false;
    }
}

/** True when the browser has offered an install prompt we are holding. */
export function canInstall() {
    return promptAvailable === true;
}

/** True when already running as an installed app, where offering install is noise. */
export function isInstalled() {
    return window.matchMedia('(display-mode: standalone)').matches
        || window.navigator.standalone === true;
}

/**
 * Shows the held install prompt. Returns "accepted", "dismissed", or "unavailable".
 * The prompt is single-use: the browser will not let us replay it.
 */
export async function promptInstall() {
    if (!deferredPrompt) return 'unavailable';
    try {
        deferredPrompt.prompt();
        const choice = await deferredPrompt.userChoice;
        deferredPrompt = null;
        promptAvailable = false;
        return choice && choice.outcome === 'accepted' ? 'accepted' : 'dismissed';
    } catch {
        deferredPrompt = null;
        promptAvailable = false;
        return 'unavailable';
    }
}
