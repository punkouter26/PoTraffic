// Fix #10 — Forward Blazor's hidden #blazor-error-ui to /api/diag/client-error.
//
// The framework toggles display on that div whenever a renderer or circuit
// exception bubbles past the error boundary. Without this observer, the only
// signal ops gets is the browser console; with it, every unhandled error
// reaches Serilog + App Insights as a structured event.
(function () {
    'use strict';
    if (window.__poTrafficErrorReporter) return; // idempotent
    window.__poTrafficErrorReporter = true;

    const ENDPOINT = '/api/diag/client-error';
    let lastReportAt = 0;
    const MIN_INTERVAL_MS = 1000; // throttle: at most 1 report/sec

    function send(payload) {
        const now = Date.now();
        if (now - lastReportAt < MIN_INTERVAL_MS) return;
        lastReportAt = now;
        try {
            fetch(ENDPOINT, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload),
                // keepalive lets the request survive page reload
                keepalive: true,
            }).catch(() => { /* swallow — reporter must never throw */ });
        } catch (_) { /* same */ }
    }

    function snapshot() {
        const ui = document.getElementById('blazor-error-ui');
        if (!ui) return null;
        const style = window.getComputedStyle(ui);
        return { visible: style.display !== 'none' && style.visibility !== 'hidden', text: ui.innerText };
    }

    // Observer: any change to #blazor-error-ui subtree or attributes.
    const target = document.getElementById('blazor-error-ui') || document.body;
    const observer = new MutationObserver(() => {
        const snap = snapshot();
        if (snap && snap.visible) {
            send({
                url: location.href,
                userAgent: navigator.userAgent,
                appVersion: window.__poTrafficAppVersion || 'unknown',
                message: snap.text || '(no message)',
                stack: new Error().stack || null,
            });
        }
    });
    observer.observe(target, { childList: true, subtree: true, attributes: true });

    // Also catch uncaught errors and unhandled rejections globally.
    window.addEventListener('error', (e) => {
        send({
            url: location.href,
            userAgent: navigator.userAgent,
            appVersion: window.__poTrafficAppVersion || 'unknown',
            message: e.message || '(uncaught error)',
            stack: (e.error && e.error.stack) || null,
        });
    });
    window.addEventListener('unhandledrejection', (e) => {
        send({
            url: location.href,
            userAgent: navigator.userAgent,
            appVersion: window.__poTrafficAppVersion || 'unknown',
            message: 'Unhandled rejection: ' + (e.reason && (e.reason.message || e.reason.toString()) || '(unknown)'),
            stack: (e.reason && e.reason.stack) || null,
        });
    });
})();
