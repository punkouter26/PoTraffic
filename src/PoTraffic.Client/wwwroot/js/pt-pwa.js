// pt-pwa.js — service-worker lifecycle and install prompt.
//
// A CLASSIC script loaded from index.html, not an ES module imported from .NET, and
// that is the whole point. `beforeinstallprompt` fires within a second or two of
// navigation and is the ONLY handle on the browser's install flow — miss it and there
// is no way to ask again this page load. Blazor WASM takes considerably longer than
// that to download, boot and render its first component, so a listener attached from
// .NET reliably arrives after the event has already been and gone.
//
// So the listeners go up here, at parse time, and .NET attaches to the state this
// script has been accumulating whenever it is ready.

(function () {
    "use strict";

    let deferredInstall = null;
    let waitingWorker = null;
    let dotnetRef = null;

    // Set only when WE asked a waiting worker to take over. The controllerchange
    // event is not proof that an update happened: on a first-ever visit the freshly
    // activated worker calls clients.claim(), the controller goes from null to that
    // worker, and the event fires — reloading there throws away whatever the user was
    // in the middle of, seconds after the page first appeared.
    let updateRequested = false;

    window.addEventListener("beforeinstallprompt", function (e) {
        // Suppress the browser's own mini-infobar so the button in Settings is the
        // single place install is offered.
        e.preventDefault();
        deferredInstall = e;
        notify();
    });

    window.addEventListener("appinstalled", function () {
        deferredInstall = null;
        notify();
    });

    function notify() {
        if (!dotnetRef) return;
        dotnetRef.invokeMethodAsync("OnPwaStateChanged").catch(function () {
            // Component disposed between the event and the callback.
            dotnetRef = null;
        });
    }

    function register() {
        if (!("serviceWorker" in navigator)) return;

        navigator.serviceWorker.register("/service-worker.js").then(function (registration) {
            // Already waiting when the page loaded: the update arrived during a previous visit.
            if (registration.waiting && navigator.serviceWorker.controller) {
                waitingWorker = registration.waiting;
                notify();
            }

            registration.addEventListener("updatefound", function () {
                const installing = registration.installing;
                if (!installing) return;
                installing.addEventListener("statechange", function () {
                    // `controller` is null on the very first install. Announcing an "update"
                    // then would prompt a reload seconds after the first ever page load.
                    if (installing.state === "installed" && navigator.serviceWorker.controller) {
                        waitingWorker = installing;
                        notify();
                    }
                });
            });
        }).catch(function () {
            // No service worker (private mode, insecure origin, unsupported browser).
            // Everything else in the app is unaffected.
        });

        // The new worker took control. Reload so every asset on the page comes from
        // the same build — but ONLY when this was a handover we asked for, and only
        // once: controllerchange can fire more than once, and an unguarded reload here
        // is an infinite refresh loop.
        let reloading = false;
        navigator.serviceWorker.addEventListener("controllerchange", function () {
            if (!updateRequested || reloading) return;
            reloading = true;
            window.location.reload();
        });
    }

    window.ptPwa = {
        /** Wires the .NET service that renders the update bar and the install button. */
        attach: function (ref) {
            dotnetRef = ref;
            notify();
        },

        detach: function () {
            dotnetRef = null;
        },

        state: function () {
            return {
                canInstall: deferredInstall !== null,
                updateReady: waitingWorker !== null,
                // Both signals matter: iOS Safari reports standalone through its own property.
                installed: window.matchMedia("(display-mode: standalone)").matches
                    || window.navigator.standalone === true,
                supported: "serviceWorker" in navigator,
            };
        },

        /** Shows the browser's install dialog. Resolves true when the user accepted. */
        promptInstall: function () {
            if (!deferredInstall) return Promise.resolve(false);
            const prompt = deferredInstall;
            // The event is single-use: cleared before awaiting so a double-click cannot
            // call prompt() twice on the same event, which throws.
            deferredInstall = null;
            try {
                prompt.prompt();
                return prompt.userChoice.then(function (choice) {
                    notify();
                    return choice.outcome === "accepted";
                });
            } catch (e) {
                notify();
                return Promise.resolve(false);
            }
        },

        // ── Web Push ──────────────────────────────────────────────────────────
        // Subscriptions hang off the same service worker registered above; its push
        // handler shows the notification.

        /** "unsupported" | "denied" | "subscribed" | "off" */
        pushState: async function () {
            if (!("serviceWorker" in navigator) || !("PushManager" in window) || !("Notification" in window))
                return "unsupported";
            if (Notification.permission === "denied") return "denied";
            const reg = await navigator.serviceWorker.getRegistration();
            const sub = reg && await reg.pushManager.getSubscription();
            return sub ? "subscribed" : "off";
        },

        /** Asks permission and subscribes. Resolves [endpoint, p256dh, auth], or null when declined. */
        subscribePush: async function (vapidPublicKey) {
            if ((await Notification.requestPermission()) !== "granted") return null;
            const reg = await navigator.serviceWorker.ready;
            const sub = await reg.pushManager.getSubscription() || await reg.pushManager.subscribe({
                userVisibleOnly: true,
                applicationServerKey: base64UrlToBytes(vapidPublicKey),
            });
            const keys = sub.toJSON().keys;
            // An array rather than an object: trim-safe to read back in .NET.
            return [sub.endpoint, keys.p256dh, keys.auth];
        },

        /** Unsubscribes this browser. Resolves the endpoint that was dropped, or null. */
        unsubscribePush: async function () {
            const reg = await navigator.serviceWorker.getRegistration();
            const sub = reg && await reg.pushManager.getSubscription();
            if (!sub) return null;
            const endpoint = sub.endpoint;
            await sub.unsubscribe();
            return endpoint;
        },

        /** Unread count on the installed app's icon. Unsupported browsers ignore it. */
        setBadge: function (count) {
            if (!("setAppBadge" in navigator)) return;
            (count > 0 ? navigator.setAppBadge(count) : navigator.clearAppBadge()).catch(function () { });
        },

        /** Applies a waiting update. The controllerchange handler above does the reload. */
        applyUpdate: function () {
            if (!waitingWorker) return;
            updateRequested = true;
            waitingWorker.postMessage("SKIP_WAITING");
            waitingWorker = null;
            notify();
        },
    };

    function base64UrlToBytes(value) {
        const padded = value + "=".repeat((4 - value.length % 4) % 4);
        const raw = atob(padded.replace(/-/g, "+").replace(/_/g, "/"));
        return Uint8Array.from(raw, (c) => c.charCodeAt(0));
    }

    register();
})();
