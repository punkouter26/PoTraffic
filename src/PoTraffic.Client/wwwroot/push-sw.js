// PoTraffic Web Push service worker (#1). Registered by js/pt-push.js.
self.addEventListener('push', (event) => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; }
    catch { data = { body: event.data ? event.data.text() : '' }; }

    const title = data.title || 'PoTraffic';
    const options = {
        body: data.body || '',
        icon: '/icon-192.png',
        badge: '/icon-192.png',
        tag: data.kind || 'potraffic',
        data: { routeId: data.routeId || null }
    };
    event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', (event) => {
    event.notification.close();
    const routeId = event.notification.data && event.notification.data.routeId;
    const url = routeId ? `/routes/${routeId}` : '/dashboard';
    event.waitUntil(
        self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((wins) => {
            for (const w of wins) {
                if ('focus' in w) { w.navigate(url); return w.focus(); }
            }
            if (self.clients.openWindow) return self.clients.openWindow(url);
        })
    );
});
