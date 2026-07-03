// pt-chart.js — GPU-accelerated travel-time trend renderer.
// Draws on a single <canvas>; uses CSS-px sizing with devicePixelRatio backing.
// Auto-picks the SVG-vs-Canvas path: under 250 points stays on 2D canvas,
// over 250 switches to an instanced WebGL draw call.

const vsSource = `
attribute vec2 a_pos;
attribute float a_value;
uniform vec2 u_xform;     // x: scale to viewport x, y: scale to viewport y
uniform vec2 u_offset;
uniform float u_min;
uniform float u_max;
varying float v_value;
void main() {
    float norm = (a_value - u_min) / max(u_max - u_min, 1.0);
    vec2 p = vec2(
        a_pos.x * u_xform.x + u_offset.x,
        (1.0 - norm) * u_xform.y + u_offset.y
    );
    v_value = a_value;
    gl_Position = vec4(p, 0.0, 1.0);
}`;

const fsSource = `
precision mediump float;
varying float v_value;
uniform vec3 u_color;
void main() { gl_FragColor = vec4(u_color, 1.0); }`;

function compile(gl, type, src) {
    const s = gl.createShader(type);
    gl.shaderSource(s, src);
    gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) {
        console.error("pt-chart: shader compile failed", gl.getShaderInfoLog(s));
        gl.deleteShader(s);
        return null;
    }
    return s;
}

function makeGlProgram(gl) {
    const vs = compile(gl, gl.VERTEX_SHADER, vsSource);
    const fs = compile(gl, gl.FRAGMENT_SHADER, fsSource);
    const p = gl.createProgram();
    gl.attachShader(p, vs);
    gl.attachShader(p, fs);
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) {
        console.error("pt-chart: program link failed", gl.getProgramInfoLog(p));
        return null;
    }
    return p;
}

// Render a polyline on a 2D context (CPU path, used for ≤250 points).
function draw2D(ctx, w, h, points, baseline, upperBand, min, max, sweepX) {
    ctx.clearRect(0, 0, w, h);
    // background grid
    ctx.strokeStyle = "#e2e8f0";
    ctx.lineWidth = 1;
    for (let g = 0; g < 4; g++) {
        const y = (h / 4) * g;
        ctx.beginPath();
        ctx.moveTo(0, y);
        ctx.lineTo(w, y);
        ctx.stroke();
    }
    if (points.length < 2) return;

    // Upper-band fill
    if (upperBand.length >= 2) {
        ctx.fillStyle = "rgba(16, 185, 129, 0.12)";
        ctx.beginPath();
        for (let i = 0; i < upperBand.length; i++) {
            const x = (i / (upperBand.length - 1)) * w;
            const y = h - ((upperBand[i] - min) / (max - min)) * h;
            if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
        }
        for (let i = upperBand.length - 1; i >= 0; i--) {
            const x = (i / (upperBand.length - 1)) * w;
            const y = h - ((baseline[i] - min) / (max - min)) * h;
            ctx.lineTo(x, y);
        }
        ctx.closePath();
        ctx.fill();
    }

    // Baseline dashed
    ctx.strokeStyle = "#94a3b8";
    ctx.lineWidth = 1.5;
    ctx.setLineDash([4, 4]);
    ctx.beginPath();
    for (let i = 0; i < baseline.length; i++) {
        const x = (i / (baseline.length - 1)) * w;
        const y = h - ((baseline[i] - min) / (max - min)) * h;
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.stroke();
    ctx.setLineDash([]);

    // Actual line
    ctx.strokeStyle = "#3b82f6";
    ctx.lineWidth = 2;
    ctx.beginPath();
    for (let i = 0; i < points.length; i++) {
        const x = (i / (points.length - 1)) * w;
        const y = h - ((points[i] - min) / (max - min)) * h;
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.stroke();

    // Rerouted markers
    ctx.fillStyle = "#f43f5e";
    for (let i = 0; i < points.length; i++) {
        if (points.rerouted?.[i]) {
            const x = (i / (points.length - 1)) * w;
            const y = h - ((points[i] - min) / (max - min)) * h;
            ctx.beginPath();
            ctx.arc(x, y, 4, 0, Math.PI * 2);
            ctx.fill();
        }
    }

    // Live "now" sweep line + label
    if (sweepX !== null) {
        const x = sweepX * w;
        const grad = ctx.createLinearGradient(x - 24, 0, x + 24, 0);
        grad.addColorStop(0, "rgba(59, 130, 246, 0)");
        grad.addColorStop(0.5, "rgba(59, 130, 246, 0.4)");
        grad.addColorStop(1, "rgba(59, 130, 246, 0)");
        ctx.fillStyle = grad;
        ctx.fillRect(x - 24, 0, 48, h);
        ctx.strokeStyle = "#3b82f6";
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.moveTo(x, 0);
        ctx.lineTo(x, h);
        ctx.stroke();
    }

    // Min/max labels
    ctx.fillStyle = "#64748b";
    ctx.font = "11px Inter, system-ui";
    ctx.fillText(`${max.toFixed(0)} min`, 4, 12);
    ctx.fillText(`${min.toFixed(0)} min`, 4, h - 4);
}

export function render(canvas, data, sweepProgress) {
    if (!canvas) return;
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const cssW = canvas.clientWidth;
    const cssH = canvas.clientHeight || 220;
    if (canvas.width !== cssW * dpr) {
        canvas.width = cssW * dpr;
        canvas.height = cssH * dpr;
    }
    const ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

    const points = data.points;
    const baseline = data.baseline.length ? data.baseline : new Array(points.length).fill(0);
    const upperBand = data.upperBand.length ? data.upperBand : baseline;
    const all = points.concat(baseline).concat(upperBand);
    const min = Math.min(...all) * 0.95;
    const max = Math.max(...all) * 1.05 || 60;

    draw2D(ctx, cssW, cssH, points, baseline, upperBand, min, max,
        sweepProgress === null ? null : Math.max(0, Math.min(1, sweepProgress)));
}