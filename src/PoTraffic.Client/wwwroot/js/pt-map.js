// pt-map.js — route map renderer built on Leaflet + OpenStreetMap tiles.
//
// Why OSM and not the Google Maps JS API: the app's Google key lives on the server
// and is used for geocoding and travel times. Putting a key in the browser to render
// tiles would expose it, bill per map load, and need referrer restrictions configured
// before the first page view. OSM raster tiles need no key and no account, and the
// road SHAPE still comes from Google — fetched server-side, once per route.
//
// The line's colour is the app's own verdict (clear / normal / slow / heavy), computed
// from this route's samples against its own baseline. It is deliberately one colour for
// the whole line: PoTraffic measures the trip end to end, so pretending to know which
// individual road is jammed would be a picture the data cannot support.

import * as fx from "./pt-fx.js";
import { createFlow } from "./pt-flow.js";

/** @type {WeakMap<HTMLElement, object>} */
const maps = new WeakMap();

/** Leaflet layer class for the animated flow. Built once, after Leaflet has loaded. */
let FlowLayer = null;

/** Leaflet is a classic script, not a module. Loaded once, shared by every map. */
let leafletLoad = null;

function loadLeaflet() {
    if (window.L) return Promise.resolve(window.L);
    return leafletLoad ??= new Promise((resolve, reject) => {
        const s = document.createElement("script");
        s.src = "lib/leaflet/leaflet.js";
        s.onload = () => resolve(window.L);
        s.onerror = () => reject(new Error("Leaflet failed to load"));
        document.head.appendChild(s);
    });
}

/**
 * Decodes Google's encoded-polyline format (precision 5) into [lat, lng] pairs.
 *
 * Leaflet has no decoder of its own and the plugin that adds one is another
 * dependency to vendor and cache; the algorithm is twenty lines, so it lives here.
 */
function decodePolyline(encoded) {
    const points = [];
    let index = 0, lat = 0, lng = 0;

    while (index < encoded.length) {
        // Each coordinate is a zig-zag-encoded delta, 5 bits per character.
        for (let i = 0; i < 2; i++) {
            let result = 0, shift = 0, byte;
            do {
                byte = encoded.charCodeAt(index++) - 63;
                result |= (byte & 0x1f) << shift;
                shift += 5;
            } while (byte >= 0x20);

            const delta = (result & 1) ? ~(result >> 1) : (result >> 1);
            if (i === 0) lat += delta; else lng += delta;
        }
        points.push([lat / 1e5, lng / 1e5]);
    }
    return points;
}

/**
 * Resolves a CSS colour EXPRESSION against the live cascade — same trick pt-chart.js
 * uses. The design tokens are light-dark() expressions, which Leaflet hands straight to
 * SVG where they are silently ignored; bouncing them off a throwaway element turns them
 * into an rgb() triple the renderer understands.
 */
function resolveColour(host, expr, fallback) {
    const probe = document.createElement("span");
    probe.style.cssText = "position:absolute;left:-9999px;top:0;width:0;height:0";
    probe.style.color = expr;
    host.appendChild(probe);
    const value = getComputedStyle(probe).color;
    probe.remove();
    return value && value.length ? value : fallback;
}

/** Traffic verdict → the token that paints it. Falls back to a literal so a missing token still draws. */
const LEVEL_TOKENS = {
    clear: ["var(--pt-traffic-clear)", "#15803d"],
    normal: ["var(--pt-traffic-normal)", "#2563eb"],
    slow: ["var(--pt-traffic-slow)", "#b45309"],
    heavy: ["var(--pt-traffic-heavy)", "#b91c1c"],
    unknown: ["var(--pt-traffic-unknown)", "#64748b"],
};

/**
 * Draws (or redraws) the route in `container`.
 *
 * @param {HTMLElement} container
 * @param {{encodedPolyline: string|null, originLat: number, originLng: number,
 *          destinationLat: number, destinationLng: number, trafficLevel: string,
 *          isApproximate: boolean, originLabel: string, destinationLabel: string}} data
 */
export async function render(container, data) {
    if (!container) return;

    let L;
    try {
        L = await loadLeaflet();
    } catch {
        // No map is not a page failure — the timings above it are the real content.
        return;
    }

    // The element can be torn down while Leaflet is still loading (fast navigation).
    if (!container.isConnected) return;

    let state = maps.get(container);
    if (!state) {
        const map = L.map(container, {
            zoomControl: true,
            attributionControl: true,
            // The page scrolls; a wheel that silently zooms the map instead of the page
            // is the single most complained-about behaviour of an embedded map.
            scrollWheelZoom: false,
        });

        L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
            maxZoom: 18,
            attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
        }).addTo(map);

        state = { map, line: null, markers: [], flow: null };
        maps.set(container, state);
    }

    const { map } = state;

    // Clear the previous drawing rather than stacking a new line on each refresh.
    if (state.line) { state.line.remove(); state.line = null; }
    state.markers.forEach((m) => m.remove());
    state.markers = [];

    const origin = [data.originLat, data.originLng];
    const destination = [data.destinationLat, data.destinationLng];

    const path = data.encodedPolyline
        ? decodePolyline(data.encodedPolyline)
        : [origin, destination];

    const [expr, fallback] = LEVEL_TOKENS[data.trafficLevel] ?? LEVEL_TOKENS.unknown;
    // Probe outside the map container: Leaflet owns that subtree and reacts to
    // children being added to it. The tokens live on :root, so any element resolves them.
    const colour = resolveColour(container.parentElement ?? document.body, expr, fallback);

    state.line = L.polyline(path, {
        color: colour,
        weight: 5,
        opacity: 0.9,
        // A dashed line is how the map admits it is guessing: no road geometry was
        // available, so this is the straight line between the two addresses.
        dashArray: data.isApproximate ? "8 8" : null,
        lineJoin: "round",
        lineCap: "round",
    }).addTo(map);

    const pin = (latlng, label, cls) => {
        const marker = L.marker(latlng, {
            icon: L.divIcon({
                className: "pt-map-pin " + cls,
                html: '<span class="pt-map-pin-dot"></span>',
                iconSize: [16, 16],
                iconAnchor: [8, 8],
            }),
            keyboard: false,
            alt: label,
        }).addTo(map);
        marker.bindTooltip(label, { direction: "top", offset: [0, -10] });
        state.markers.push(marker);
    };

    pin(origin, data.originLabel || "Start", "pt-map-pin-origin");
    pin(destination, data.destinationLabel || "End", "pt-map-pin-dest");

    // The route's verdict becomes the app's mood, so the background wash and the
    // ambient audio agree with the line the user is looking at.
    fx.setMood(data.trafficLevel);

    // Particles travelling the path. Purely decorative — if anything here fails the
    // map is still a map, so it is wrapped and never allowed to break the render.
    try {
        FlowLayer ??= createFlow(L);
        if (fx.animates()) {
            if (state.flow) {
                state.flow.setPath(path, data.trafficLevel, expr);
            } else {
                state.flow = new FlowLayer({ latlngs: path, level: data.trafficLevel, colour: expr });
                state.flow.addTo(map);
            }
        } else if (state.flow) {
            map.removeLayer(state.flow);
            state.flow = null;
        }
    } catch { /* decoration only */ }

    map.fitBounds(state.line.getBounds(), { padding: [28, 28] });

    // Leaflet measures the container on creation. Inside a card that was still laying
    // out (or a <details> that just opened) that measurement is stale and the tiles
    // render into a strip. Re-measuring after layout settles is the documented fix.
    requestAnimationFrame(() => {
        if (!container.isConnected) return;
        map.invalidateSize();
        map.fitBounds(state.line.getBounds(), { padding: [28, 28] });
    });
}

/** Tears the map down. Leaflet leaks tile listeners and a resize observer otherwise. */
export function destroy(container) {
    const state = maps.get(container);
    if (!state) return;
    // The flow layer holds a GL context and a ticker registration; removing the map
    // fires its onRemove, which releases both.
    try { state.map.remove(); } catch { /* already gone */ }
    maps.delete(container);
}
