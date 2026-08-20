// pt-ribbon.js — the route as a 3D ribbon.
//
// The route's real geographic shape becomes a curve in the ground plane. The ribbon
// riding that curve RISES with travel time and takes the verdict colour at each
// point, so a bad commute is literally a hill.
//
// WHAT THIS IS AND IS NOT CLAIMING. PoTraffic measures the trip end to end; it does
// not know which individual road was slow. So the height is the day's samples laid
// along the route IN ORDER, not a per-street measurement — a metaphor, and the
// caption in RouteRibbon.razor says so in as many words. Pretending otherwise would
// be a beautiful picture of data that does not exist.
//
// LOADING: Three.js is ~750KB across two files and is imported dynamically, only
// when this view is actually opened. A user who never taps "3D" never downloads it.
// The service worker caches it after the first open, so the second is instant.

import * as fx from "./pt-fx.js";

let THREE = null;

/** @type {WeakMap<HTMLElement, object>} */
const scenes = new WeakMap();

const LEVEL_COLOURS = {
    clear: 0x22c55e,
    normal: 0x3b82f6,
    slow: 0xf59e0b,
    heavy: 0xef4444,
    unknown: 0x94a3b8,
};

async function loadThree() {
    return THREE ??= await import("../lib/three/three.module.min.js");
}

/**
 * Projects lat/lng onto a local metre-ish plane centred on the path, then normalises
 * to roughly ±1 so the camera framing does not depend on how long the commute is.
 *
 * A full map projection would be wrong here anyway — this is a sculpture of a route,
 * not a map, and it is never overlaid on one.
 */
function project(latlngs) {
    const lats = latlngs.map((p) => p[0]);
    const lngs = latlngs.map((p) => p[1]);
    const midLat = (Math.min(...lats) + Math.max(...lats)) / 2;
    const midLng = (Math.min(...lngs) + Math.max(...lngs)) / 2;

    // Longitude degrees shrink with latitude; without this a north-south commute
    // comes out stretched.
    const cos = Math.cos((midLat * Math.PI) / 180);

    const raw = latlngs.map((p) => [(p[1] - midLng) * cos, p[0] - midLat]);
    const extent = Math.max(
        1e-6,
        ...raw.map(([x, z]) => Math.max(Math.abs(x), Math.abs(z))));

    return raw.map(([x, z]) => [(x / extent) * 1.5, (z / extent) * 1.5]);
}

/**
 * Samples → heights, scaled against the day's own spread.
 *
 * The ceiling is deliberately low relative to the path, which `project` normalises to
 * roughly ±1.5. Mapping the worst sample to full height instead made a sharp jam
 * render as a near-vertical wall — technically the data, but unreadable as terrain,
 * and it hid the shape of everything either side of the peak. A third of the route's
 * own width is enough for a peak to be unmistakable and still look like a hill.
 */
const HEIGHT_FLOOR = 0.05;
const HEIGHT_CEILING = 0.5;

function heights(samples) {
    if (!samples || samples.length === 0) return null;
    const min = Math.min(...samples);
    const max = Math.max(...samples);
    const span = max - min;
    // A flat day is a flat ribbon sitting low, not a ribbon of undefined height.
    if (span < 1e-6) return samples.map(() => HEIGHT_FLOOR + 0.04);
    return samples.map((v) =>
        HEIGHT_FLOOR + ((v - min) / span) * (HEIGHT_CEILING - HEIGHT_FLOOR));
}

/** Height at path fraction t, resampled from however many samples exist. */
function heightAt(hs, t) {
    if (!hs) return HEIGHT_FLOOR + 0.04;
    if (hs.length === 1) return hs[0];
    const exact = t * (hs.length - 1);
    const i = Math.min(hs.length - 2, Math.floor(exact));
    const f = exact - i;
    return hs[i] + (hs[i + 1] - hs[i]) * f;
}

export async function render(container, data) {
    if (!container) return;

    let T;
    try {
        T = await loadThree();
    } catch {
        return;   // Three.js unavailable: the 2D map above is the real content.
    }
    if (!container.isConnected) return;

    let state = scenes.get(container);
    if (!state) {
        state = build(T, container);
        if (!state) return;
        scenes.set(container, state);
    }

    updateRibbon(T, state, data);
    frameCamera(state);

    if (!state.unregister && fx.animates()) {
        state.unregister = fx.register(`ribbon-${state.id}`, (dt) => step(state, dt));
    }
    // Reduced motion still gets the object, just not the drift.
    renderOnce(state);
}

let sceneCounter = 0;

function build(T, container) {
    const renderer = tryRenderer(T, container);
    if (!renderer) return null;

    const scene = new T.Scene();

    const camera = new T.PerspectiveCamera(38, 1, 0.1, 100);
    camera.position.set(0, 2.2, 4.2);

    // Three lights, each doing one job: a key from above-front for form, a cool rim
    // from behind for the silhouette, and a dim ambient so the shadow side is not
    // pure black. Any fewer and the ribbon reads as a flat coloured shape.
    scene.add(new T.AmbientLight(0xffffff, 0.55));

    const key = new T.DirectionalLight(0xffffff, 1.5);
    key.position.set(2.5, 4, 3);
    scene.add(key);

    const rim = new T.DirectionalLight(0x88bbff, 0.9);
    rim.position.set(-3, 1.5, -2.5);
    scene.add(rim);

    // Ground grid: without a floor the ribbon floats in a void and its height —
    // the entire point of the view — has nothing to be measured against.
    const grid = new T.GridHelper(6, 24, 0x64748b, 0x64748b);
    grid.material.transparent = true;
    grid.material.opacity = 0.18;
    scene.add(grid);

    const state = {
        id: ++sceneCounter,
        container, renderer, scene, camera, grid,
        ribbon: null, shadow: null,
        // Orbit state. A hand-rolled controller rather than vendoring OrbitControls:
        // this needs drag-to-orbit and nothing else, and that is thirty lines.
        yaw: -0.5, pitch: 0.55, distance: 4.6,
        targetYaw: -0.5, targetPitch: 0.55,
        dragging: false, lastX: 0, lastY: 0,
        idle: 0,
        unregister: null,
        resizeObserver: null,
    };

    attachOrbit(state);

    state.resizeObserver = new ResizeObserver(() => { resize(state); renderOnce(state); });
    state.resizeObserver.observe(container);
    resize(state);

    return state;
}

function tryRenderer(T, container) {
    try {
        const renderer = new T.WebGLRenderer({ antialias: !fx.isDegraded(), alpha: true });
        renderer.setPixelRatio(fx.dpr());
        // Tone mapping plus sRGB output: without these the emissive ribbon clips to
        // flat white at its peaks instead of glowing.
        renderer.toneMapping = T.ACESFilmicToneMapping;
        renderer.toneMappingExposure = 1.15;
        renderer.outputColorSpace = T.SRGBColorSpace;
        container.appendChild(renderer.domElement);
        return renderer;
    } catch {
        return null;
    }
}

function updateRibbon(T, state, data) {
    const { scene } = state;

    for (const old of [state.ribbon, state.shadow]) {
        if (!old) continue;
        scene.remove(old);
        old.geometry.dispose();
        old.material.dispose();
    }

    const flat = project(data.path);
    if (flat.length < 2) return;

    const hs = heights(data.samples);
    const colour = new T.Color(LEVEL_COLOURS[data.level] ?? LEVEL_COLOURS.unknown);

    // A Catmull-Rom through the projected vertices, resampled evenly. The raw
    // polyline has wildly uneven spacing — dense at junctions, sparse on a motorway —
    // and extruding that directly gives a ribbon that bunches and stretches.
    const spine = new T.CatmullRomCurve3(
        flat.map(([x, z]) => new T.Vector3(x, 0, z)), false, "centripetal", 0.4);

    const STEPS = fx.isDegraded() ? 80 : 220;
    const points = [];
    for (let i = 0; i <= STEPS; i++) {
        const t = i / STEPS;
        const p = spine.getPoint(t);
        points.push(new T.Vector3(p.x, heightAt(hs, t), p.z));
    }

    const curve = new T.CatmullRomCurve3(points, false, "centripetal", 0.5);
    const geometry = new T.TubeGeometry(curve, STEPS, 0.045, 8, false);

    // Vertex colours along the length: cool at the low points, hot at the peaks, so
    // the shape and the colour tell the same story and neither is redundant.
    tintByHeight(T, geometry, colour);

    const material = new T.MeshStandardMaterial({
        vertexColors: true,
        roughness: 0.28,
        metalness: 0.12,
        emissive: colour,
        emissiveIntensity: 0.35,
    });

    state.ribbon = new T.Mesh(geometry, material);
    scene.add(state.ribbon);

    // A flattened copy on the floor. Cheaper than a shadow map, always readable, and
    // it doubles as the route's plan view — which is what makes the height legible.
    const shadowGeometry = new T.TubeGeometry(
        new T.CatmullRomCurve3(points.map((p) => new T.Vector3(p.x, 0.002, p.z)), false, "centripetal", 0.5),
        STEPS, 0.03, 5, false);
    // Tinted with the verdict rather than black: a black shadow is invisible on the
    // dark theme's near-black stage, which is exactly where the floor track is most
    // needed to make the height readable.
    const shadowMaterial = new T.MeshBasicMaterial({
        color: colour, transparent: true, opacity: 0.3, depthWrite: false,
    });
    state.shadow = new T.Mesh(shadowGeometry, shadowMaterial);
    scene.add(state.shadow);
}

/** Darkens the ribbon toward its low points by mixing the verdict colour with slate. */
function tintByHeight(T, geometry, colour) {
    const position = geometry.attributes.position;
    const colours = new Float32Array(position.count * 3);

    let min = Infinity, max = -Infinity;
    for (let i = 0; i < position.count; i++) {
        const y = position.getY(i);
        if (y < min) min = y;
        if (y > max) max = y;
    }
    const span = Math.max(1e-6, max - min);

    const low = new T.Color(0x1e293b);
    const mixed = new T.Color();

    for (let i = 0; i < position.count; i++) {
        const t = (position.getY(i) - min) / span;
        mixed.copy(low).lerp(colour, 0.35 + t * 0.65);
        colours[i * 3] = mixed.r;
        colours[i * 3 + 1] = mixed.g;
        colours[i * 3 + 2] = mixed.b;
    }

    geometry.setAttribute("color", new T.BufferAttribute(colours, 3));
}

function attachOrbit(state) {
    const el = state.renderer.domElement;
    el.style.touchAction = "pan-y";   // vertical page scroll still works over the canvas

    const down = (e) => {
        state.dragging = true;
        state.idle = 0;
        state.lastX = e.clientX;
        state.lastY = e.clientY;
        el.setPointerCapture?.(e.pointerId);
    };

    const move = (e) => {
        if (!state.dragging) return;
        state.targetYaw += (e.clientX - state.lastX) * 0.008;
        // Clamped so the camera cannot pass under the floor or over the top, both of
        // which leave the viewer with no idea which way is up.
        state.targetPitch = Math.max(0.12, Math.min(1.35,
            state.targetPitch + (e.clientY - state.lastY) * 0.006));
        state.lastX = e.clientX;
        state.lastY = e.clientY;
        state.idle = 0;
        if (!fx.animates()) { applyCamera(state); renderOnce(state); }
    };

    const up = (e) => {
        state.dragging = false;
        el.releasePointerCapture?.(e.pointerId);
    };

    el.addEventListener("pointerdown", down);
    el.addEventListener("pointermove", move);
    el.addEventListener("pointerup", up);
    el.addEventListener("pointercancel", up);
    el.addEventListener("pointerleave", up);

    state.detachOrbit = () => {
        el.removeEventListener("pointerdown", down);
        el.removeEventListener("pointermove", move);
        el.removeEventListener("pointerup", up);
        el.removeEventListener("pointercancel", up);
        el.removeEventListener("pointerleave", up);
    };
}

function frameCamera(state) {
    applyCamera(state);
}

function applyCamera(state) {
    const { camera, yaw, pitch, distance } = state;
    camera.position.set(
        Math.sin(yaw) * Math.cos(pitch) * distance,
        Math.sin(pitch) * distance,
        Math.cos(yaw) * Math.cos(pitch) * distance);
    camera.lookAt(0, 0.35, 0);
}

function step(state, dt) {
    if (!state.container.isConnected) { destroyState(state); return; }

    // Resume a slow drift once the user has left it alone for a moment. An object
    // that turns by itself invites the drag; one that sits still looks like an image.
    if (!state.dragging) {
        state.idle += dt;
        if (state.idle > 2.5) state.targetYaw += dt * 0.16;
    }

    // Eased toward the target so a flung drag settles instead of stopping dead.
    state.yaw += (state.targetYaw - state.yaw) * Math.min(1, dt * 6);
    state.pitch += (state.targetPitch - state.pitch) * Math.min(1, dt * 6);

    applyCamera(state);
    renderOnce(state);
}

function renderOnce(state) {
    try { state.renderer.render(state.scene, state.camera); }
    catch { /* context lost; nothing to salvage */ }
}

function resize(state) {
    const { container, renderer, camera } = state;
    const w = container.clientWidth || 1;
    const h = container.clientHeight || 1;
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
}

function destroyState(state) {
    state.unregister?.();
    state.unregister = null;
    state.detachOrbit?.();
    state.resizeObserver?.disconnect();

    state.scene.traverse((object) => {
        object.geometry?.dispose?.();
        if (Array.isArray(object.material)) object.material.forEach((m) => m.dispose());
        else object.material?.dispose?.();
    });

    // Frees the GPU context immediately; without it a few navigations exhaust the
    // browser's hard limit on live WebGL contexts and every canvas on the page dies.
    state.renderer.dispose();
    state.renderer.forceContextLoss?.();
    state.renderer.domElement.remove();
}

export function destroy(container) {
    const state = scenes.get(container);
    if (!state) return;
    destroyState(state);
    scenes.delete(container);
}
