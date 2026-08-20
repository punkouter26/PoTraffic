// pt-particles.js — the celebratory burst when a commute comes back better than usual.
//
// WHY NOT A PHYSICS ENGINE: Rapier is roughly a megabyte of WebAssembly, and this app
// now ships as an installable PWA whose whole appeal is that it works on a phone with
// no signal. Paying a megabyte of download so that confetti can collide with itself —
// which nobody will ever notice at 1.1 seconds and 60% opacity — would be a bad trade
// made loudly. What confetti actually needs is gravity, drag and a spin, which is the
// forty lines below.
//
// One shared canvas, one buffer, one ticker slot, and it unregisters itself the frame
// the last particle dies — an idle page pays nothing.

import * as fx from "./pt-fx.js";

const GRAVITY = 1500;      // px/s², tuned so a burst settles in about a second
const DRAG = 0.86;         // per-second velocity retention; air, roughly
const LIFETIME = 1.15;     // seconds

let canvas = null;
let ctx = null;
let particles = [];
let unregister = null;

function ensureCanvas() {
    if (canvas) return canvas;

    canvas = document.createElement("canvas");
    canvas.className = "pt-particles";
    canvas.setAttribute("aria-hidden", "true");
    document.body.appendChild(canvas);
    ctx = canvas.getContext("2d");
    window.addEventListener("resize", resize, { passive: true });
    resize();
    return canvas;
}

function resize() {
    if (!canvas) return;
    const scale = fx.dpr();
    canvas.width = Math.round(window.innerWidth * scale);
    canvas.height = Math.round(window.innerHeight * scale);
    canvas.style.width = `${window.innerWidth}px`;
    canvas.style.height = `${window.innerHeight}px`;
}

/** Confetti palette: the app's own accent ramp, so a burst still looks like this app. */
function palette(host) {
    return [
        fx.resolveCss(host, "var(--pt-traffic-clear)", "#15803d"),
        fx.resolveCss(host, "var(--pt-traffic-normal)", "#2563eb"),
        fx.resolveCss(host, "var(--pt-color-brand-500)", "#3b82f6"),
        fx.resolveCss(host, "var(--pt-color-success)", "#10b981"),
    ];
}

/**
 * Fires a burst from a screen point.
 *
 * @param {{x:number, y:number}} origin  viewport coordinates; defaults to centre-top
 * @param {number} count                 particle count before degradation
 */
export function burst(origin, count = 70) {
    if (!fx.animates()) return;   // reduced motion: the result is still reported in text

    ensureCanvas();

    const scale = fx.dpr();
    const n = fx.isDegraded() ? Math.round(count / 3) : count;
    const colours = palette(canvas.parentElement);

    const ox = (origin?.x ?? window.innerWidth / 2) * scale;
    const oy = (origin?.y ?? window.innerHeight * 0.3) * scale;

    for (let i = 0; i < n; i++) {
        // Fired into an upward cone rather than a full circle: confetti thrown at the
        // floor is just a smudge, and the arc is what reads as celebration.
        const angle = -Math.PI / 2 + (Math.random() - 0.5) * 1.9;
        const speed = (320 + Math.random() * 520) * scale;

        particles.push({
            x: ox,
            y: oy,
            vx: Math.cos(angle) * speed,
            vy: Math.sin(angle) * speed,
            // Rectangles, not dots: a spinning rectangle catches the eye by flashing
            // between edge-on and face-on, which a circle cannot do.
            w: (5 + Math.random() * 5) * scale,
            h: (2.5 + Math.random() * 4) * scale,
            spin: (Math.random() - 0.5) * 18,
            angle: Math.random() * Math.PI,
            colour: colours[i % colours.length],
            life: LIFETIME * (0.7 + Math.random() * 0.5),
            age: 0,
        });
    }

    // Bound the pool. A user hammering "Probe now" must not accumulate ten thousand
    // rectangles and take the frame rate with them.
    if (particles.length > 400) particles = particles.slice(-400);

    unregister ??= fx.register("particles", step);
}

function step(dt) {
    if (!ctx) return;

    ctx.clearRect(0, 0, canvas.width, canvas.height);

    // Frame-rate-independent drag: raising the per-second retention to the power of
    // the elapsed time is what keeps the motion identical at 30fps and at 144fps.
    const drag = Math.pow(DRAG, dt);
    const scale = fx.dpr();
    let alive = 0;

    for (const p of particles) {
        p.age += dt;
        if (p.age >= p.life) continue;
        alive++;

        p.vx *= drag;
        p.vy = p.vy * drag + GRAVITY * scale * dt;
        p.x += p.vx * dt;
        p.y += p.vy * dt;
        p.angle += p.spin * dt;

        // Fade over the last third only — fading from the first frame makes the burst
        // look weak at the exact moment it should look strongest.
        const t = p.age / p.life;
        ctx.globalAlpha = t < 0.66 ? 1 : 1 - (t - 0.66) / 0.34;

        ctx.save();
        ctx.translate(p.x, p.y);
        ctx.rotate(p.angle);
        // Scaling the height by |cos| fakes the rectangle turning in 3D — the flash
        // that sells the spin, for the price of one cosine.
        ctx.fillStyle = p.colour;
        ctx.fillRect(-p.w / 2, -p.h / 2, p.w, p.h * Math.abs(Math.cos(p.angle * 1.7)));
        ctx.restore();
    }

    ctx.globalAlpha = 1;

    if (alive === 0) {
        particles = [];
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        unregister?.();
        unregister = null;
    } else if (alive < particles.length) {
        // Compact once rather than splicing inside the loop.
        particles = particles.filter((p) => p.age < p.life);
    }
}

/** Fires from an element's centre — the usual case, celebrating a specific card. */
export function burstFrom(element, count) {
    if (!element) { burst(null, count); return; }
    const r = element.getBoundingClientRect();
    burst({ x: r.left + r.width / 2, y: r.top + r.height / 2 }, count);
}

export function destroy() {
    unregister?.();
    unregister = null;
    particles = [];
    window.removeEventListener("resize", resize);
    canvas?.remove();
    canvas = null;
    ctx = null;
}
