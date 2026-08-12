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

/** @type {WeakMap<HTMLCanvasElement, object>} */
const states = new WeakMap();

/** Minimum horizontal drag, in CSS px, before a gesture counts as a brush not a click. */
const BRUSH_THRESHOLD_PX = 8;

function readTheme(canvas) {
    const cs = getComputedStyle(canvas);
    const pick = (name, fallback) => {
        const v = cs.getPropertyValue(name).trim();
        return v.length ? v : fallback;
    };
    return {
        grid: pick("--pt-border-subtle", "#e2e8f0"),
        muted: pick("--pt-fg-muted", "#64748b"),
        baseline: pick("--pt-fg-tertiary", "#94a3b8"),
        line: pick("--pt-color-brand-500", "#3b82f6"),
        lineStrong: pick("--pt-color-brand-600", "#2563eb"),
        danger: pick("--pt-color-danger", "#f43f5e"),
        // Second series in compare mode (#8). Warning-amber rather than another blue:
        // the two lines have to be told apart at a glance, including in greyscale.
        compare: pick("--pt-color-warning", "#f59e0b"),
        band: "rgba(16, 185, 129, 0.12)",
        brush: "rgba(59, 130, 246, 0.16)"
    };
}

function xForIndex(i, count, w) {
    return count <= 1 ? 0 : (i / (count - 1)) * w;
}

function yForValue(v, min, max, h) {
    const span = max - min;
    return span <= 0 ? h / 2 : h - ((v - min) / span) * h;
}

function drawSeries(ctx, values, count, min, max, w, h) {
    for (let i = 0; i < values.length; i++) {
        const x = xForIndex(i, count, w);
        const y = yForValue(values[i], min, max, h);
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
}

function draw(canvas) {
    const st = states.get(canvas);
    if (!st) return;

    const { ctx, w, h, view, theme } = st;
    const points = view.points;
    const count = points.length;

    ctx.clearRect(0, 0, w, h);

    // Horizontal gridlines
    ctx.strokeStyle = theme.grid;
    ctx.lineWidth = 1;
    for (let g = 0; g < 4; g++) {
        const y = (h / 4) * g;
        ctx.beginPath();
        ctx.moveTo(0, y);
        ctx.lineTo(w, y);
        ctx.stroke();
    }

    if (count < 2) return;

    const { min, max } = view;

    // Brush selection wash, painted under the data
    if (st.brush && Math.abs(st.brush.x2 - st.brush.x1) >= BRUSH_THRESHOLD_PX) {
        const x1 = Math.min(st.brush.x1, st.brush.x2);
        const x2 = Math.max(st.brush.x1, st.brush.x2);
        ctx.fillStyle = theme.brush;
        ctx.fillRect(x1, 0, x2 - x1, h);
    }

    // ±1σ band between baseline and upper band
    if (view.upperBand.length >= 2 && view.baseline.length >= 2) {
        ctx.fillStyle = theme.band;
        ctx.beginPath();
        drawSeries(ctx, view.upperBand, view.upperBand.length, min, max, w, h);
        for (let i = view.baseline.length - 1; i >= 0; i--) {
            ctx.lineTo(
                xForIndex(i, view.baseline.length, w),
                yForValue(view.baseline[i], min, max, h));
        }
        ctx.closePath();
        ctx.fill();
    }

    // Baseline, dashed
    if (view.baseline.length >= 2) {
        ctx.strokeStyle = theme.baseline;
        ctx.lineWidth = 1.5;
        ctx.setLineDash([4, 4]);
        ctx.beginPath();
        drawSeries(ctx, view.baseline, view.baseline.length, min, max, w, h);
        ctx.stroke();
        ctx.setLineDash([]);
    }

    // Observed travel times
    ctx.strokeStyle = theme.line;
    ctx.lineWidth = 2;
    ctx.beginPath();
    drawSeries(ctx, points, count, min, max, w, h);
    ctx.stroke();

    // Second route, in compare mode (#8). Drawn over the primary at the same x-scale —
    // the caller guarantees both series describe the same time slots.
    if (view.compare.length >= 2) {
        ctx.strokeStyle = theme.compare;
        ctx.lineWidth = 2;
        ctx.beginPath();
        drawSeries(ctx, view.compare, view.compare.length, min, max, w, h);
        ctx.stroke();
    }

    // Reroute markers
    ctx.fillStyle = theme.danger;
    for (let i = 0; i < count; i++) {
        if (!view.rerouted[i]) continue;
        ctx.beginPath();
        ctx.arc(xForIndex(i, count, w), yForValue(points[i], min, max, h), 4, 0, Math.PI * 2);
        ctx.fill();
    }

    // Crosshair on the hovered sample
    if (st.hoverIndex !== null && st.hoverIndex >= 0 && st.hoverIndex < count) {
        const x = xForIndex(st.hoverIndex, count, w);
        const y = yForValue(points[st.hoverIndex], min, max, h);

        ctx.strokeStyle = theme.lineStrong;
        ctx.globalAlpha = 0.35;
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(x, 0);
        ctx.lineTo(x, h);
        ctx.stroke();
        ctx.globalAlpha = 1;

        ctx.fillStyle = theme.lineStrong;
        ctx.beginPath();
        ctx.arc(x, y, 4.5, 0, Math.PI * 2);
        ctx.fill();
        ctx.strokeStyle = "#fff";
        ctx.lineWidth = 1.5;
        ctx.stroke();
    }

    // Axis labels
    ctx.fillStyle = theme.muted;
    ctx.font = "11px Inter, system-ui";
    ctx.fillText(`${max.toFixed(0)} min`, 4, 12);
    ctx.fillText(`${min.toFixed(0)} min`, 4, h - 4);
}

function buildView(data) {
    const points = data.points ?? [];
    const baseline = data.baseline ?? [];
    const upperBand = data.upperBand ?? [];
    const compare = data.compare ?? [];
    const all = points.concat(baseline).concat(upperBand).concat(compare);

    // An all-empty chart still needs a sane axis so gridlines and labels render.
    const min = all.length ? Math.min(...all) * 0.95 : 0;
    const max = all.length ? Math.max(...all) * 1.05 : 60;

    return {
        points,
        baseline,
        upperBand,
        compare,
        rerouted: data.rerouted ?? [],
        labels: data.labels ?? [],
        // Series names label the tooltip rows in compare mode.
        names: data.names ?? [],
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
    const rows = [`<strong>${escapeHtml(label)}</strong>`, `${minutes.toFixed(0)} min`];

    // Compare mode (#8): name both series and say which one wins this slot, rather
    // than leaving the reader to match line colours against a legend.
    const other = view.compare[i];
    if (typeof other === "number") {
        const nameA = escapeHtml(view.names[0] ?? "Route A");
        const nameB = escapeHtml(view.names[1] ?? "Route B");
        const gap = other - minutes;

        rows[1] = `${nameA}: ${minutes.toFixed(0)} min`;
        rows.push(`${nameB}: ${other.toFixed(0)} min`);
        rows.push(Math.abs(gap) < 0.5
            ? "neck and neck"
            : `<strong>${gap > 0 ? nameA : nameB}</strong> faster by ${Math.abs(gap).toFixed(0)} min`);
    }

    // Deviation from the baseline, expressed in σ when the band gives us one.
    const base = view.baseline[i];
    if (typeof base === "number" && base > 0) {
        const sigma = (view.upperBand[i] ?? base) - base;
        const delta = minutes - base;
        if (sigma > 0.01) {
            const z = delta / sigma;
            const dir = z >= 0 ? "above" : "below";
            rows.push(`${Math.abs(z).toFixed(1)}σ ${dir} baseline`);
        } else {
            rows.push(`${delta >= 0 ? "+" : ""}${delta.toFixed(0)} min vs baseline`);
        }
    }

    if (view.rerouted[i]) rows.push(`<span class="pt-chart-tip-flag">rerouted</span>`);

    tip.innerHTML = rows.join("<br>");
    tip.classList.add("visible");

    // Flip the tooltip to the left of the cursor near the right edge so it
    // never runs off the card.
    const x = xForIndex(i, view.points.length, st.w);
    const flip = x > st.w - 130;
    tip.style.left = `${flip ? x - 12 : x + 12}px`;
    tip.style.transform = flip ? "translateX(-100%)" : "none";
}

function indexAt(canvas, clientX) {
    const st = states.get(canvas);
    if (!st) return null;
    const rect = canvas.getBoundingClientRect();
    const count = st.view.points.length;
    if (count === 0 || rect.width === 0) return null;
    const ratio = (clientX - rect.left) / rect.width;
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

    states.set(canvas, {
        ...previous,
        ctx,
        w: cssW,
        h: cssH,
        view,
        theme: readTheme(canvas),
        // A hover index from a longer previous series would point at nothing.
        hoverIndex: previous.hoverIndex !== undefined
            && previous.hoverIndex !== null
            && previous.hoverIndex < view.points.length
            ? previous.hoverIndex
            : null,
        brush: previous.brush ?? null
    });

    draw(canvas);
    updateTooltip(canvas);
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
            st.brush = { x1: st.dragStartX, x2: e.clientX - canvas.getBoundingClientRect().left };
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
        st.dragStartX = e.clientX - canvas.getBoundingClientRect().left;
        st.dragStartIndex = indexAt(canvas, e.clientX);
        st.brush = null;
        canvas.setPointerCapture?.(e.pointerId);
    };

    const onUp = (e) => {
        const st = states.get(canvas);
        if (!st || !st.dragging) return;
        st.dragging = false;
        canvas.releasePointerCapture?.(e.pointerId);

        const endX = e.clientX - canvas.getBoundingClientRect().left;
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
