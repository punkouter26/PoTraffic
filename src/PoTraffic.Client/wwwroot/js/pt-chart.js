// pt-chart.js — travel-time trend renderer with pointer interaction.
//
// Draws on a single <canvas> with CSS-px sizing and devicePixelRatio backing.
//
// Hover, crosshair and tooltip are handled entirely in JS against state cached
// per canvas: a mousemove that crossed into .NET would cost an interop round-trip
// per frame. Only decisions the app needs to act on — a point click, a committed
// brush selection — are sent back.
//
// Colours resolve from the design tokens on the element, so the chart follows the
// light/dark theme instead of baking in one palette.

import * as fx from "./pt-fx.js";

/** @type {WeakMap<HTMLCanvasElement, object>} */
const states = new WeakMap();

/** Minimum horizontal drag, in CSS px, before a gesture counts as a brush not a click. */
const BRUSH_THRESHOLD_PX = 8;

/**
 * The bloom, as passes over the same path. Widest and faintest first so the halo
 * builds outward; the opaque stroke is drawn separately, after these.
 * Dropped to a single cheap pass once the frame budget has been missed.
 */
const BLOOM_FULL = [
    { width: 9, blur: 18, alpha: 0.10 },
    { width: 5, blur: 10, alpha: 0.16 },
    { width: 3, blur: 5, alpha: 0.22 },
];
const BLOOM_CHEAP = [{ width: 5, blur: 8, alpha: 0.18 }];

/** How long the draw-in takes. Long enough to read as motion, short enough to ignore. */
const REVEAL_SECONDS = 0.85;

/**
 * The first `fraction` of a series, with the final point interpolated so the leading
 * end advances smoothly instead of snapping from sample to sample.
 */
function revealed(values, fraction) {
    if (values.length < 2) return values;

    const exact = fraction * (values.length - 1);
    const whole = Math.floor(exact);
    const rest = exact - whole;

    const out = values.slice(0, whole + 1);
    if (whole + 1 < values.length) {
        out.push(values[whole] + (values[whole + 1] - values[whole]) * rest);
    }
    return out;
}

/**
 * Gutters reserved around the plot for the axes. The left one holds the minutes
 * scale, the bottom one the 24-hour clock; the data never draws into either, so
 * a tick label can't be overpainted by the line it labels.
 */
const PAD = { top: 12, right: 14, bottom: 26, left: 46 };

/**
 * Resolves a colour EXPRESSION (not a raw token name) against the live cascade.
 *
 * getComputedStyle().getPropertyValue("--pt-fg-muted") hands back the *specified*
 * text — for this app's tokens that is the literal string "light-dark(...)", which
 * canvas cannot parse. Assigning an unparseable value to ctx.strokeStyle is a
 * silent no-op, so the axis labels inherited whatever colour was set last (the
 * reroute-marker red) and the gridlines came out brand blue. Bouncing each
 * expression off a throwaway element makes the browser resolve it to an rgb()
 * triple the way it would for any real element.
 */
function readTheme(canvas) {
    const host = canvas.parentElement ?? document.body;
    const probe = document.createElement("span");
    probe.style.cssText = "position:absolute;left:-9999px;top:0;width:0;height:0";
    host.appendChild(probe);

    const resolve = (expr, fallback) => {
        probe.style.color = "";
        probe.style.color = expr;
        const v = getComputedStyle(probe).color;
        return v && v.length ? v : fallback;
    };

    const theme = {
        grid: resolve("var(--pt-border-subtle)", "#e2e8f0"),
        axis: resolve("var(--pt-border-strong)", "#cbd5e1"),
        muted: resolve("var(--pt-fg-muted)", "#64748b"),
        baseline: resolve("var(--pt-fg-tertiary)", "#94a3b8"),
        surface: resolve("var(--pt-bg-elev-1)", "#ffffff"),
        line: resolve("var(--pt-color-brand-500)", "#3b82f6"),
        lineStrong: resolve("var(--pt-color-brand-600)", "#2563eb"),
        danger: resolve("var(--pt-color-danger)", "#f43f5e"),
        // Second series in compare mode (#8). Warning-amber rather than another blue:
        // the two lines have to be told apart at a glance, including in greyscale.
        compare: resolve("var(--pt-color-warning)", "#f59e0b"),
        band: "rgba(16, 185, 129, 0.12)",
        brush: "rgba(59, 130, 246, 0.16)"
    };

    probe.remove();
    return theme;
}

/** The data rectangle, in CSS px, inside the axis gutters. */
function plotOf(w, h) {
    return {
        x: PAD.left,
        y: PAD.top,
        w: Math.max(10, w - PAD.left - PAD.right),
        h: Math.max(10, h - PAD.top - PAD.bottom)
    };
}

function xForIndex(i, count, plot) {
    return plot.x + (count <= 1 ? plot.w / 2 : (i / (count - 1)) * plot.w);
}

function yForValue(v, min, max, plot) {
    const span = max - min;
    return plot.y + (span <= 0 ? plot.h / 2 : plot.h - ((v - min) / span) * plot.h);
}

function drawSeries(ctx, values, count, min, max, plot) {
    for (let i = 0; i < values.length; i++) {
        const x = xForIndex(i, count, plot);
        const y = yForValue(values[i], min, max, plot);
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
}

/**
 * Round tick values covering [min, max] at a 1/2/5×10ⁿ step — so the minutes
 * scale reads 40, 50, 60 rather than 41.7, 52.1, 62.5.
 */
function niceTicks(min, max, target) {
    const span = max - min;
    if (!(span > 0)) return [min];

    const magnitude = Math.pow(10, Math.floor(Math.log10(span / target)));
    const normalised = (span / target) / magnitude;
    const step = (normalised <= 1 ? 1 : normalised <= 2 ? 2 : normalised <= 5 ? 5 : 10) * magnitude;

    const ticks = [];
    for (let v = Math.ceil(min / step) * step; v <= max + step * 1e-6; v += step) {
        ticks.push(Math.round(v * 100) / 100);
    }
    return ticks;
}

/**
 * Sample indices to label along the bottom, spaced so the 24-hour stamps never
 * collide. Always includes the first and last sample — the span of the chart is
 * the first thing the reader wants.
 */
function xTickIndices(count, plotWidth) {
    if (count <= 1) return count === 1 ? [0] : [];

    const maxTicks = Math.max(2, Math.min(8, Math.floor(plotWidth / 68)));
    if (count <= maxTicks) return Array.from({ length: count }, (_, i) => i);

    const stride = (count - 1) / (maxTicks - 1);
    const indices = [];
    for (let t = 0; t < maxTicks; t++) {
        const i = Math.round(t * stride);
        if (indices[indices.length - 1] !== i) indices.push(i);
    }
    return indices;
}

function draw(canvas) {
    const st = states.get(canvas);
    if (!st) return;

    const { ctx, w, h, view, theme } = st;
    const points = view.points;
    const count = points.length;
    const plot = plotOf(w, h);
    const { min, max } = view;

    ctx.clearRect(0, 0, w, h);
    ctx.font = "11px Inter, system-ui, sans-serif";

    // ── Y axis: travel time in whole minutes ──────────────────────────────
    // Gridlines and their labels are drawn together so a line can never appear
    // without the number that explains it.
    ctx.lineWidth = 1;
    ctx.textAlign = "right";
    ctx.textBaseline = "middle";
    for (const value of niceTicks(min, max, 4)) {
        const y = Math.round(yForValue(value, min, max, plot)) + 0.5;

        ctx.strokeStyle = theme.grid;
        ctx.beginPath();
        ctx.moveTo(plot.x, y);
        ctx.lineTo(plot.x + plot.w, y);
        ctx.stroke();

        ctx.fillStyle = theme.muted;
        ctx.fillText(value.toFixed(0), plot.x - 8, y);
    }

    // Unit, stated once above the scale rather than repeated on every tick.
    ctx.fillStyle = theme.muted;
    ctx.textAlign = "left";
    ctx.textBaseline = "alphabetic";
    ctx.fillText("min", 4, plot.y - 2);

    // ── X axis: 24-hour clock ─────────────────────────────────────────────
    ctx.textAlign = "center";
    ctx.textBaseline = "top";
    for (const i of xTickIndices(count, plot.w)) {
        const x = xForIndex(i, count, plot);
        const label = view.labels[i];
        if (!label) continue;

        ctx.strokeStyle = theme.grid;
        ctx.beginPath();
        ctx.moveTo(Math.round(x) + 0.5, plot.y + plot.h);
        ctx.lineTo(Math.round(x) + 0.5, plot.y + plot.h + 4);
        ctx.stroke();

        ctx.fillStyle = theme.muted;
        // Clamped so the first and last stamps stay inside the canvas instead of
        // being clipped by their own centring.
        ctx.fillText(label, Math.min(Math.max(x, 18), w - 18), plot.y + plot.h + 7);
    }

    // Axis rules — the plot's floor and left edge.
    ctx.strokeStyle = theme.axis;
    ctx.beginPath();
    ctx.moveTo(plot.x + 0.5, plot.y);
    ctx.lineTo(plot.x + 0.5, plot.y + plot.h + 0.5);
    ctx.lineTo(plot.x + plot.w, plot.y + plot.h + 0.5);
    ctx.stroke();

    if (count < 2) return;

    // Brush selection wash, painted under the data
    if (st.brush && Math.abs(st.brush.x2 - st.brush.x1) >= BRUSH_THRESHOLD_PX) {
        const x1 = Math.min(st.brush.x1, st.brush.x2);
        const x2 = Math.max(st.brush.x1, st.brush.x2);
        ctx.fillStyle = theme.brush;
        ctx.fillRect(x1, plot.y, x2 - x1, plot.h);
    }

    // ── Observed travel times ────────────────────────────────────────────
    //
    // Single line: the actual probed travel times. No baseline, no ±1σ band,
    // no reroute dots, no compare route. The chart answers "how long has this
    // trip been taking today?" — the dashboard card lists the best/worst from
    // the same data, and the weekly heatmap handles the comparison.
    //
    // The glow (three additive passes at falling alpha and rising blur) is the
    // only ornament: at a 2px stroke it reads as a subtle bloom rather than a
    // halo, and `st.reveal` runs 0→1 when the points change so the line draws
    // itself in instead of snapping into place.
    const reveal = st.reveal ?? 1;
    const shown = reveal >= 1 ? points : revealed(points, reveal);

    if (shown.length >= 2) {
        ctx.lineJoin = "round";
        ctx.lineCap = "round";
        ctx.globalCompositeOperation = "lighter";

        for (const pass of (fx.isDegraded() ? BLOOM_CHEAP : BLOOM_FULL)) {
            ctx.strokeStyle = theme.line;
            ctx.globalAlpha = pass.alpha;
            ctx.lineWidth = pass.width;
            ctx.shadowColor = theme.line;
            ctx.shadowBlur = pass.blur;
            ctx.beginPath();
            drawSeries(ctx, shown, count, min, max, plot);
            ctx.stroke();
        }

        ctx.shadowBlur = 0;
        ctx.globalAlpha = 1;
        ctx.globalCompositeOperation = "source-over";

        ctx.strokeStyle = theme.line;
        ctx.lineWidth = 2;
        ctx.beginPath();
        drawSeries(ctx, shown, count, min, max, plot);
        ctx.stroke();

        // Leading dot during the draw-in animation.
        if (reveal < 1) {
            const i = shown.length - 1;
            const x = xForIndex(i, count, plot);
            const y = yForValue(shown[i], min, max, plot);
            ctx.save();
            ctx.globalCompositeOperation = "lighter";
            ctx.fillStyle = theme.line;
            ctx.shadowColor = theme.line;
            ctx.shadowBlur = 14;
            ctx.beginPath();
            ctx.arc(x, y, 3.5, 0, Math.PI * 2);
            ctx.fill();
            ctx.restore();
        }
    }

    // Crosshair on the hovered sample
    if (st.hoverIndex !== null && st.hoverIndex >= 0 && st.hoverIndex < count) {
        const x = xForIndex(st.hoverIndex, count, plot);
        const y = yForValue(points[st.hoverIndex], min, max, plot);

        ctx.strokeStyle = theme.lineStrong;
        ctx.globalAlpha = 0.35;
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(x, plot.y);
        ctx.lineTo(x, plot.y + plot.h);
        ctx.stroke();
        ctx.globalAlpha = 1;

        ctx.fillStyle = theme.lineStrong;
        ctx.beginPath();
        ctx.arc(x, y, 4.5, 0, Math.PI * 2);
        ctx.fill();
        // Ringed in the card's own colour so the dot reads as raised on either theme;
        // a hard white ring was invisible on the light card and glaring on the dark one.
        ctx.strokeStyle = theme.surface;
        ctx.lineWidth = 1.5;
        ctx.stroke();
    }
}

function buildView(data) {
    const points = data.points ?? [];

    // An all-empty chart still needs a sane axis so gridlines and labels render.
    const min = points.length ? Math.min(...points) * 0.95 : 0;
    const max = points.length ? Math.max(...points) * 1.05 : 60;

    return {
        points,
        labels: data.labels ?? [],
        min,
        max: max > min ? max : min + 1
    };
}

/**
 * The tooltip is assembled as HTML, and series names are route addresses the user
 * typed — they do not go into innerHTML unescaped.
 */
function escapeHtml(value) {
    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;");
}

function ensureTooltip(canvas) {
    const host = canvas.parentElement;
    if (!host) return null;

    let tip = host.querySelector(".pt-chart-tip");
    if (!tip) {
        tip = document.createElement("div");
        tip.className = "pt-chart-tip";
        tip.setAttribute("role", "status");
        host.appendChild(tip);
    }
    return tip;
}

function updateTooltip(canvas) {
    const st = states.get(canvas);
    if (!st) return;

    const tip = ensureTooltip(canvas);
    if (!tip) return;

    const i = st.hoverIndex;
    const view = st.view;
    if (i === null || i < 0 || i >= view.points.length) {
        tip.classList.remove("visible");
        return;
    }

    const minutes = view.points[i];
    const label = view.labels[i] ?? `#${i + 1}`;
    tip.innerHTML = `<strong>${escapeHtml(label)}</strong><br>${minutes.toFixed(0)} min`;
    tip.classList.add("visible");

    // Flip the tooltip to the left of the cursor near the right edge so it
    // never runs off the card.
    const x = xForIndex(i, view.points.length, plotOf(st.w, st.h));
    const flip = x > st.w - 130;
    tip.style.left = `${flip ? x - 12 : x + 12}px`;
    tip.style.transform = flip ? "translateX(-100%)" : "none";
}

/** Pointer position in canvas CSS px, independent of any layout scaling. */
function localX(canvas, st, clientX) {
    const rect = canvas.getBoundingClientRect();
    if (rect.width === 0) return 0;
    return (clientX - rect.left) * (st.w / rect.width);
}

function indexAt(canvas, clientX) {
    const st = states.get(canvas);
    if (!st) return null;
    const count = st.view.points.length;
    if (count === 0) return null;

    // Measured against the plot rectangle, not the whole canvas: with the axis
    // gutters in place those differ by 60px, which is several samples of skew.
    const plot = plotOf(st.w, st.h);
    const ratio = (localX(canvas, st, clientX) - plot.x) / plot.w;
    return Math.max(0, Math.min(count - 1, Math.round(ratio * (count - 1))));
}

/**
 * Renders `data` into `canvas`. Safe to call on every parameter change; interaction
 * handlers attached by `attach` keep working across re-renders.
 */
export function render(canvas, data) {
    if (!canvas) return;

    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const cssW = canvas.clientWidth || 600;
    const cssH = canvas.clientHeight || 220;

    if (canvas.width !== Math.round(cssW * dpr) || canvas.height !== Math.round(cssH * dpr)) {
        canvas.width = Math.round(cssW * dpr);
        canvas.height = Math.round(cssH * dpr);
    }

    const ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

    const previous = states.get(canvas) ?? {};
    const view = buildView(data);

    // The reveal replays only when the SHAPE of the data changes. This page refreshes
    // every fifteen seconds; re-animating an unchanged line on every tick would be a
    // chart that never stops twitching.
    const signature = `${view.points.length}:${view.points[0] ?? ""}:${view.points[view.points.length - 1] ?? ""}`;
    const changed = signature !== previous.signature;

    states.set(canvas, {
        ...previous,
        ctx,
        w: cssW,
        h: cssH,
        view,
        signature,
        theme: readTheme(canvas),
        reveal: changed && fx.animates() && view.points.length >= 2 ? 0 : 1,
        // A hover index from a longer previous series would point at nothing.
        hoverIndex: previous.hoverIndex !== undefined
            && previous.hoverIndex !== null
            && previous.hoverIndex < view.points.length
            ? previous.hoverIndex
            : null,
        brush: previous.brush ?? null
    });

    if (changed && fx.animates() && view.points.length >= 2) startReveal(canvas);

    draw(canvas);
    updateTooltip(canvas);
}

/**
 * Runs the draw-in. Registered under a per-canvas key so two charts on one page
 * animate independently, and unregistered the moment it completes — a finished
 * chart costs nothing.
 */
function startReveal(canvas) {
    const key = `chart-reveal-${revealKey(canvas)}`;

    const stop = fx.register(key, (dt) => {
        const st = states.get(canvas);
        // The canvas can be detached mid-animation by a navigation.
        if (!st || !canvas.isConnected) { stop(); return; }

        st.reveal = Math.min(1, (st.reveal ?? 0) + dt / REVEAL_SECONDS);
        draw(canvas);

        if (st.reveal >= 1) stop();
    });
}

/** Stable per-canvas id, so re-rendering the same chart replaces its ticker slot. */
let revealCounter = 0;
const revealKeys = new WeakMap();
function revealKey(canvas) {
    let key = revealKeys.get(canvas);
    if (key === undefined) revealKeys.set(canvas, key = ++revealCounter);
    return key;
}

/**
 * Wires pointer interaction. Invokes `<method>` on `dotnetRef` with:
 *   { kind: "point", index }                — a click on a sample
 *   { kind: "brush", fromIndex, toIndex }   — a committed drag selection
 * Returns a cleanup function reference for disposal from .NET.
 */
export function attach(canvas, dotnetRef, method) {
    if (!canvas) return () => { };

    const notify = (payload) => {
        try { dotnetRef.invokeMethodAsync(method, payload); } catch { /* component gone */ }
    };

    const onMove = (e) => {
        const st = states.get(canvas);
        if (!st) return;
        st.hoverIndex = indexAt(canvas, e.clientX);
        if (st.dragging) {
            st.brush = { x1: st.dragStartX, x2: localX(canvas, st, e.clientX) };
        }
        draw(canvas);
        updateTooltip(canvas);
    };

    const onLeave = () => {
        const st = states.get(canvas);
        if (!st) return;
        st.hoverIndex = null;
        st.dragging = false;
        st.brush = null;
        draw(canvas);
        updateTooltip(canvas);
    };

    const onDown = (e) => {
        const st = states.get(canvas);
        if (!st) return;
        st.dragging = true;
        st.dragStartX = localX(canvas, st, e.clientX);
        st.dragStartIndex = indexAt(canvas, e.clientX);
        st.brush = null;
        canvas.setPointerCapture?.(e.pointerId);
    };

    const onUp = (e) => {
        const st = states.get(canvas);
        if (!st || !st.dragging) return;
        st.dragging = false;
        canvas.releasePointerCapture?.(e.pointerId);

        const endX = localX(canvas, st, e.clientX);
        const travelled = Math.abs(endX - st.dragStartX);
        const endIndex = indexAt(canvas, e.clientX);
        st.brush = null;
        draw(canvas);

        if (travelled >= BRUSH_THRESHOLD_PX && st.dragStartIndex !== null && endIndex !== null) {
            notify({
                kind: "brush",
                fromIndex: Math.min(st.dragStartIndex, endIndex),
                toIndex: Math.max(st.dragStartIndex, endIndex)
            });
        } else if (endIndex !== null) {
            notify({ kind: "point", index: endIndex });
        }
    };

    canvas.addEventListener("pointermove", onMove);
    canvas.addEventListener("pointerleave", onLeave);
    canvas.addEventListener("pointerdown", onDown);
    canvas.addEventListener("pointerup", onUp);

    return () => {
        canvas.removeEventListener("pointermove", onMove);
        canvas.removeEventListener("pointerleave", onLeave);
        canvas.removeEventListener("pointerdown", onDown);
        canvas.removeEventListener("pointerup", onUp);
        states.delete(canvas);
    };
}
