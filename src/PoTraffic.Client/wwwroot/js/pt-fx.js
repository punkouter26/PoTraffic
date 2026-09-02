// pt-fx.js — the shared runtime every visual effect in the app runs on.
//
// WHY THIS EXISTS: five effects each calling requestAnimationFrame is five callbacks
// per frame, five getComputedStyle reads, five independent ideas about whether the
// tab is visible, and five separate places to remember prefers-reduced-motion. That
// is how a smooth app becomes a janky one. Everything here is centralised so the
// answer to "are we animating, and how hard" is decided exactly once per frame.
//
// Three things live here:
//   1. ONE ticker. Effects register a step(dt, now) function and get called from a
//      single rAF loop that stops itself the moment nothing is registered.
//   2. THE MOTION LEVEL. "full" | "reduced" | "off", from the user's own setting,
//      with the OS prefers-reduced-motion preference as the floor.
//   3. THE TRAFFIC MOOD. One number, 0 (clear) to 1 (jammed), that the background
//      wash, the map flow and the ambient audio all read, so the app never says
//      "calm" in one channel and "bad" in another.

const STORAGE_MOTION = "pt-motion-level";

/** Frame budget guard. Anything slower than this and we shed work rather than stutter. */
const SLOW_FRAME_MS = 24;          // ~40fps — two consecutive misses trigger a downgrade
const SLOW_FRAMES_BEFORE_DEGRADE = 30;

const effects = new Map();
let rafId = null;
let lastTime = 0;
let slowFrames = 0;

/** Set when sustained frame times force a quality drop; effects read it to shed detail. */
let degraded = false;

const listeners = new Set();

// ── Preferences ──────────────────────────────────────────────────────────────

function read(key, fallback) {
    try {
        const v = localStorage.getItem(key);
        return v === null ? fallback : v;
    } catch {
        return fallback;   // private mode
    }
}

function write(key, value) {
    try { localStorage.setItem(key, value); } catch { /* private mode */ }
}

const reducedMedia = window.matchMedia("(prefers-reduced-motion: reduce)");

/**
 * The effective motion level.
 *
 * The OS preference is a FLOOR, not a default: someone who has asked their system
 * for reduced motion has asked every app, and an in-app "full" setting must not
 * override that. They can still choose "off" — going quieter is always allowed.
 */
export function motionLevel() {
    const chosen = read(STORAGE_MOTION, "full");
    if (chosen === "off") return "off";
    if (reducedMedia.matches) return chosen === "full" ? "reduced" : chosen;
    return chosen;
}

export function setMotionLevel(level) {
    write(STORAGE_MOTION, level);
    document.documentElement.setAttribute("data-motion", motionLevel());
    emit();
}

/** True when decorative animation should run at all. */
export function animates() {
    return motionLevel() === "full";
}

/** True when an effect may draw, even if it must not move (static gradient, still glow). */
export function draws() {
    return motionLevel() !== "off";
}

export function onChange(fn) {
    listeners.add(fn);
    return () => listeners.delete(fn);
}

function emit() {
    listeners.forEach((fn) => { try { fn(); } catch { /* listener's problem */ } });
}

reducedMedia.addEventListener("change", () => {
    document.documentElement.setAttribute("data-motion", motionLevel());
    emit();
});

// Stamped on <html> so CSS can react without asking JS anything.
document.documentElement.setAttribute("data-motion", motionLevel());

// ── The traffic mood ─────────────────────────────────────────────────────────

/** 0 = flowing freely, 1 = jammed. Null until the app has an opinion. */
let mood = null;

const MOOD_BY_LEVEL = { clear: 0.05, normal: 0.3, slow: 0.65, heavy: 0.95, unknown: null };

/**
 * Sets the app-wide mood from a traffic verdict string. Everything decorative reads
 * this rather than the verdict itself, so a new verdict name never has to be taught
 * to four different effects.
 */
export function setMood(level) {
    const next = MOOD_BY_LEVEL[level] ?? null;
    if (next === mood) return;
    mood = next;
    emit();
}

/** Current mood, or `fallback` while the app has no reading. */
export function getMood(fallback = 0.25) {
    return mood === null ? fallback : mood;
}

// ── The ticker ───────────────────────────────────────────────────────────────

function frame(now) {
    rafId = null;

    const dt = lastTime ? Math.min((now - lastTime) / 1000, 0.1) : 0.016;
    lastTime = now;

    // Sustained slow frames mean this device cannot afford what we are asking for.
    // Rather than stutter, tell effects to shed detail — they read `isDegraded()`.
    if (dt * 1000 > SLOW_FRAME_MS) {
        if (++slowFrames >= SLOW_FRAMES_BEFORE_DEGRADE && !degraded) {
            degraded = true;
            emit();
        }
    } else if (slowFrames > 0) {
        slowFrames--;
    }

    for (const [, effect] of effects) {
        // One effect throwing must not stop every other effect on the page.
        try {
            if (effect.fps) {
                effect.acc += dt;
                const period = 1 / effect.fps;
                if (effect.acc < period) continue;
                effect.step(effect.acc, now);
                effect.acc = 0;
            } else {
                effect.step(dt, now);
            }
        } catch {
            effects.delete(effect.key);
        }
    }

    if (effects.size > 0) schedule();
    else lastTime = 0;
}

function schedule() {
    if (rafId === null && !document.hidden) rafId = requestAnimationFrame(frame);
}

/**
 * Registers a per-frame callback. Returns an unregister function.
 *
 * @param {string} key      unique id; registering the same key twice replaces the first
 * @param {(dt:number, now:number)=>void} step
 * @param {{fps?: number}} [options]  cap this effect's rate — a slow background wash
 *                                    at 30fps looks identical and costs half as much
 */
export function register(key, step, options = {}) {
    effects.set(key, { key, step, fps: options.fps ?? 0, acc: 0 });
    lastTime = 0;
    schedule();
    return () => effects.delete(key);
}

export function unregister(key) {
    effects.delete(key);
}

export function isDegraded() {
    return degraded;
}

// A hidden tab must not burn a frame budget. rAF already throttles hard in most
// browsers, but a backgrounded PWA on a phone can keep ticking — this stops it dead.
document.addEventListener("visibilitychange", () => {
    if (document.hidden) {
        if (rafId !== null) cancelAnimationFrame(rafId);
        rafId = null;
        lastTime = 0;
    } else {
        schedule();
    }
});

// ── Shared helpers effects keep needing ──────────────────────────────────────

/**
 * Resolves a CSS colour EXPRESSION against the live cascade and returns [r,g,b] in 0–1.
 *
 * The design tokens are `light-dark(...)` expressions. Canvas and WebGL cannot parse
 * those — assigning one is a silent no-op that leaves the previous colour in place.
 * Bouncing the expression off a throwaway element makes the browser resolve it the
 * way it would for any real element.
 */
export function resolveRgb(host, expr, fallback = [0.23, 0.51, 0.96]) {
    const probe = document.createElement("span");
    probe.style.cssText = "position:absolute;left:-9999px;top:0;width:0;height:0";
    probe.style.color = expr;
    (host ?? document.body).appendChild(probe);
    const value = getComputedStyle(probe).color;
    probe.remove();

    const m = value.match(/-?[\d.]+/g);
    if (!m || m.length < 3) return fallback;
    return [m[0] / 255, m[1] / 255, m[2] / 255];
}

/** Same, but returns the browser-resolved CSS string for canvas2D use. */
export function resolveCss(host, expr, fallback = "#3b82f6") {
    const probe = document.createElement("span");
    probe.style.cssText = "position:absolute;left:-9999px;top:0;width:0;height:0";
    probe.style.color = expr;
    (host ?? document.body).appendChild(probe);
    const value = getComputedStyle(probe).color;
    probe.remove();
    return value || fallback;
}

/** Device pixel ratio, capped. Above 2 the cost squares and nobody can see it. */
export function dpr() {
    return Math.min(window.devicePixelRatio || 1, isDegraded() ? 1 : 2);
}

/**
 * Compiles a WebGL2 program. Returns null — never throws — when the context, the
 * source or the link fails, so every caller can fall back to canvas2D or to nothing.
 */
export function buildProgram(gl, vertexSource, fragmentSource) {
    const compile = (type, source) => {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
            gl.deleteShader(shader);
            return null;
        }
        return shader;
    };

    const vs = compile(gl.VERTEX_SHADER, vertexSource);
    const fs = compile(gl.FRAGMENT_SHADER, fragmentSource);
    if (!vs || !fs) return null;

    const program = gl.createProgram();
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);
    gl.deleteShader(vs);
    gl.deleteShader(fs);

    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        gl.deleteProgram(program);
        return null;
    }
    return program;
}

/** A WebGL2 context with the flags these effects want, or null if unavailable. */
export function gl2(canvas) {
    try {
        return canvas.getContext("webgl2", {
            alpha: true,
            antialias: false,          // we anti-alias in the shader; MSAA here is wasted fill
            depth: false,
            stencil: false,
            premultipliedAlpha: true,
            powerPreference: "low-power",
            // NOT desynchronized. That flag opts into a low-latency swap chain meant
            // for stylus input; on Windows/ANGLE with a real GPU it takes the canvas
            // off the normal compositor path, which rendered this overlay as opaque
            // black over the map tiles and flickered. Software rasterisers ignore the
            // flag entirely, which is why it survived headless testing.
        });
    } catch {
        return null;
    }
}
