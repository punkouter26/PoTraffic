// Web Push subscription helpers (#1). Imported as an ES module by NotificationBell.razor.

function urlBase64ToUint8Array(base64String) {
    const padding = '='.repeat((4 - (base64String.length % 4)) % 4);
    const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
    const raw = atob(base64);
    const arr = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) arr[i] = raw.charCodeAt(i);
    return arr;
}

export function isSupported() {
    return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
}

export function permission() {
    return typeof Notification !== 'undefined' ? Notification.permission : 'unsupported';
}

// Requests permission, registers the SW, subscribes, and returns { endpoint, p256dh, auth }.
// Returns null if unsupported or the user declined.
export async function subscribe(vapidPublicKey) {
    if (!isSupported()) return null;
    const perm = await Notification.requestPermission();
    if (perm !== 'granted') return null;

    const reg = await navigator.serviceWorker.register('/push-sw.js');
    await navigator.serviceWorker.ready;

    let sub = await reg.pushManager.getSubscription();
    if (!sub) {
        sub = await reg.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey: urlBase64ToUint8Array(vapidPublicKey)
        });
    }
    const json = sub.toJSON();
    // Returned as an array (trim-safe for Blazor interop): [endpoint, p256dh, auth].
    return [sub.endpoint, json.keys.p256dh, json.keys.auth];
}

export async function unsubscribe() {
    if (!isSupported()) return null;
    const reg = await navigator.serviceWorker.getRegistration('/push-sw.js');
    if (!reg) return null;
    const sub = await reg.pushManager.getSubscription();
    if (!sub) return null;
    const endpoint = sub.endpoint;
    await sub.unsubscribe();
    return { endpoint };
}
