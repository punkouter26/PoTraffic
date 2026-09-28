// pt-terrain.js — the weekly heatmap as a landscape.
//
// The same numbers as the grid, drawn as a WebGL2 height field: days run front to
// back, the travelled quarter-hours left to right, and HEIGHT is how much slower a
// slot runs than the route's typical time. The peaks are the slots to avoid; a flat
// plain is a commute that holds steady. Slots whose spread makes them unpredictable
// shimmer, which is the grid's corner dot turned into something you notice.
//
// It is an alternative view, never the only one: the grid stays in the DOM for screen
// readers and for exact figures, and every failure here returns false so the caller
// falls back to it.

import * as fx from "./pt-fx.js";

const VERT = `#version 300 es
in vec3 a_pos;
in vec3 a_normal;
in float a_vol;
in float a_data;

uniform mat4 u_mvp;

out vec3  v_normal;
out float v_height;
out float v_vol;
out float v_data;
out vec2  v_ground;

void main() {
    v_normal = a_normal;
    v_height = a_pos.y;
    v_vol = a_vol;
    v_data = a_data;
    v_ground = a_pos.xz;
    gl_Position = u_mvp * vec4(a_pos, 1.0);
}`;

const FRAG = `#version 300 es
precision mediump float;

uniform vec3  u_low;       // --pt-color-success
uniform vec3  u_mid;       // --pt-color-warning
uniform vec3  u_high;      // --pt-color-danger
uniform vec3  u_empty;     // no samples
uniform float u_peak;      // height of the tallest possible slot
uniform float u_time;

in vec3  v_normal;
in float v_height;
in float v_vol;
in float v_data;
in vec2  v_ground;

out vec4 fragColor;

void main() {
    float h = clamp(v_height / u_peak, 0.0, 1.0);
    vec3 ramp = h < 0.35 ? mix(u_low, u_mid, h / 0.35) : mix(u_mid, u_high, (h - 0.35) / 0.65);
    vec3 base = mix(u_empty, ramp, v_data);

    vec3 n = normalize(v_normal);
    float light = 0.5 + 0.5 * max(dot(n, normalize(vec3(0.35, 1.0, 0.45))), 0.0);

    // Contour lines, as on a topographic map: they make the slope readable where
    // shading alone flattens out. fwidth keeps them one pixel wide at any zoom.
    // Masked off the flat plain, which sits exactly on the zero contour.
    float c = v_height / u_peak * 10.0;
    float line = smoothstep(0.5 - fwidth(c) * 1.5, 0.5, abs(fract(c) - 0.5)) * smoothstep(0.01, 0.05, h);

    // Unpredictable slots shimmer. Travelling in x and z so it reads as light moving
    // over the surface, not as the surface blinking.
    float shimmer = v_vol * (0.5 + 0.5 * sin(u_time * 3.0 + v_ground.x * 9.0 + v_ground.y * 6.0));

    vec3 colour = base * light + shimmer * 0.28 + line * 0.12 * v_data;
    fragColor = vec4(colour, 1.0);
}`;

/** Mesh vertices per cell edge. Enough to round the peaks; more is fill cost nobody sees. */
const SUBDIV = 6;
const SUBDIV_DEGRADED = 3;

/** Ratio (mean ÷ typical) that reaches full height. Matches the grid's darkest band (> 1.70). */
const RATIO_AT_PEAK = 1.8;

/** Coefficient of variation the grid calls unpredictable (VolatilityHeatmap.VolatileCoefficient). */
const VOLATILE_CV = 0.08;

/** @type {WeakMap<HTMLCanvasElement, object>} */
const states = new WeakMap();

// ── Tiny matrix kit (column-major, as WebGL wants) ──────────────────────────

function perspective(fovY, aspect, near, far) {
    const f = 1 / Math.tan(fovY / 2), nf = 1 / (near - far);
    return [f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) * nf, -1, 0, 0, 2 * far * near * nf, 0];
}

function lookAt(eye, target) {
    const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    const norm = (v) => { const l = Math.hypot(...v) || 1; return v.map((x) => x / l); };
    const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    const z = norm(sub(eye, target)), x = norm(cross([0, 1, 0], z)), y = cross(z, x);
    return [x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, -dot(x, eye), -dot(y, eye), -dot(z, eye), 1];
}

function multiply(a, b) {
    const out = new Array(16);
    for (let c = 0; c < 4; c++)
        for (let r = 0; r < 4; r++)
            out[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
    return out;
}

function project(m, [x, y, z]) {
    const cx = m[0] * x + m[4] * y + m[8] * z + m[12];
    const cy = m[1] * x + m[5] * y + m[9] * z + m[13];
    const cw = m[3] * x + m[7] * y + m[11] * z + m[15];
    return [cx / cw, cy / cw];
}

// ── Mesh ─────────────────────────────────────────────────────────────────────

/**
 * Builds the height field. Cell values sit at cell centres and are blended with
 * smoothstep weights, so each slot is a rounded hill rather than a block — and the
 * summit of each hill is still exactly that slot's value.
 */
function buildMesh(data, subdiv) {
    const cols = data.cols.length, rows = data.rows.length;
    const cell = 2 / Math.max(cols, rows * 1.6);     // longest side spans 2 units
    const depthCell = cell * 1.6;                      // days are deeper than slots are wide
    const width = cols * cell, depth = rows * depthCell;
    const peak = Math.min(width, depth) * 0.45 + 0.25;

    const at = (arr, r, c) => arr[Math.min(rows - 1, Math.max(0, r)) * cols + Math.min(cols - 1, Math.max(0, c))];
    const height = (r, c) => {
        const ratio = at(data.ratio, r, c);
        return ratio < 0 ? 0 : Math.max(0, Math.min(1, (ratio - 1) / (RATIO_AT_PEAK - 1))) * peak;
    };
    const vol = (r, c) => {
        const cv = at(data.cv, r, c);
        return Math.max(0, Math.min(1, (cv - VOLATILE_CV * 0.6) / (VOLATILE_CV * 0.9)));
    };
    const has = (r, c) => (at(data.ratio, r, c) < 0 ? 0 : 1);

    const smooth = (t) => t * t * (3 - 2 * t);
    const sample = (fn, u, v) => {
        // u, v in cell units, measured from the first cell's centre.
        const c0 = Math.floor(u), r0 = Math.floor(v);
        const fu = smooth(u - c0), fv = smooth(v - r0);
        return (fn(r0, c0) * (1 - fu) + fn(r0, c0 + 1) * fu) * (1 - fv)
             + (fn(r0 + 1, c0) * (1 - fu) + fn(r0 + 1, c0 + 1) * fu) * fv;
    };

    const nx = cols * subdiv + 1, nz = rows * subdiv + 1;
    const heights = new Float32Array(nx * nz);
    const verts = new Float32Array(nx * nz * 8);   // pos3, normal3, vol, data

    for (let j = 0; j < nz; j++) {
        for (let i = 0; i < nx; i++) {
            const u = i / subdiv - 0.5, v = j / subdiv - 0.5;
            heights[j * nx + i] = sample(height, u, v);
        }
    }

    const hx = width / (nx - 1), hz = depth / (nz - 1);
    for (let j = 0; j < nz; j++) {
        for (let i = 0; i < nx; i++) {
            const k = j * nx + i, o = k * 8;
            const u = i / subdiv - 0.5, v = j / subdiv - 0.5;

            // Normal from central differences on the height grid.
            const hl = heights[j * nx + Math.max(0, i - 1)], hr = heights[j * nx + Math.min(nx - 1, i + 1)];
            const hd = heights[Math.max(0, j - 1) * nx + i], hu = heights[Math.min(nz - 1, j + 1) * nx + i];
            const n = [(hl - hr) / (2 * hx), 1, (hd - hu) / (2 * hz)];
            const len = Math.hypot(...n);

            verts[o] = -width / 2 + i * hx;
            verts[o + 1] = heights[k];
            verts[o + 2] = -depth / 2 + j * hz;
            verts[o + 3] = n[0] / len;
            verts[o + 4] = n[1] / len;
            verts[o + 5] = n[2] / len;
            verts[o + 6] = sample(vol, u, v);
            verts[o + 7] = sample(has, u, v);
        }
    }

    const indices = new Uint32Array((nx - 1) * (nz - 1) * 6);
    let p = 0;
    for (let j = 0; j < nz - 1; j++) {
        for (let i = 0; i < nx - 1; i++) {
            const a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
            indices.set([a, c, b, b, c, d], p);
            p += 6;
        }
    }

    return { verts, indices, width, depth, peak, cell, depthCell };
}

// ── Public API ───────────────────────────────────────────────────────────────

/**
 * Draws the terrain into `canvas`, with axis labels placed into `labels`.
 * Returns false when WebGL2 is unavailable, so the caller can stay on the grid.
 *
 * @param {HTMLCanvasElement} canvas
 * @param {HTMLElement} labels
 * @param {{cols: string[], rows: string[], ratio: number[], cv: number[]}} data
 *        ratio and cv are row-major (rows × cols); ratio is -1 where a slot has no samples.
 */
export function render(canvas, labels, data) {
    // A data refresh redraws into the same canvas. Its context must survive that:
    // getContext on a canvas whose context was deliberately lost returns the dead one.
    teardown(canvas, false);
    if (!canvas || !data?.cols?.length || !data?.rows?.length) return false;

    const gl = fx.gl2(canvas);
    if (!gl) return false;
    const program = fx.buildProgram(gl, VERT, FRAG);
    if (!program) return false;

    const mesh = buildMesh(data, fx.isDegraded() ? SUBDIV_DEGRADED : SUBDIV);

    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
    const vbo = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
    gl.bufferData(gl.ARRAY_BUFFER, mesh.verts, gl.STATIC_DRAW);
    const bind = (name, size, offset) => {
        const loc = gl.getAttribLocation(program, name);
        if (loc < 0) return;
        gl.enableVertexAttribArray(loc);
        gl.vertexAttribPointer(loc, size, gl.FLOAT, false, 32, offset);
    };
    bind("a_pos", 3, 0);
    bind("a_normal", 3, 12);
    bind("a_vol", 1, 24);
    bind("a_data", 1, 28);
    const ibo = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ibo);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, mesh.indices, gl.STATIC_DRAW);

    gl.enable(gl.DEPTH_TEST);
    gl.useProgram(program);

    const host = canvas.parentElement;
    const u = (name) => gl.getUniformLocation(program, name);
    const uniforms = { mvp: u("u_mvp"), time: u("u_time") };
    gl.uniform3fv(u("u_low"), fx.resolveRgb(host, "var(--pt-color-success)", [0.09, 0.64, 0.29]));
    gl.uniform3fv(u("u_mid"), fx.resolveRgb(host, "var(--pt-color-warning)", [0.85, 0.47, 0.02]));
    gl.uniform3fv(u("u_high"), fx.resolveRgb(host, "var(--pt-color-danger)", [0.86, 0.15, 0.15]));
    gl.uniform3fv(u("u_empty"), fx.resolveRgb(host, "var(--pt-fg-faint)", [0.6, 0.64, 0.7]));
    gl.uniform1f(u("u_peak"), mesh.peak);

    // Axis labels: plain DOM over the canvas, re-projected whenever the camera moves.
    labels.replaceChildren();
    const mark = (text, pos, cls) => {
        const el = document.createElement("span");
        el.className = "pt-terrain-label " + cls;
        el.textContent = text;
        labels.appendChild(el);
        return { el, pos };
    };
    const left = -mesh.width / 2 - mesh.cell * 0.4;
    const front = mesh.depth / 2 + mesh.depthCell * 0.35;
    const colX = (c) => -mesh.width / 2 + (c + 0.5) * mesh.cell;
    const marks = data.rows.map((d, r) =>
        mark(d.slice(0, 3), [left, 0, -mesh.depth / 2 + (r + 0.5) * mesh.depthCell], "pt-terrain-day"));
    const last = data.cols.length - 1;
    [...new Set([0, Math.floor(last / 2), last])].forEach((c) =>
        marks.push(mark(data.cols[c], [colX(c), 0, front], "pt-terrain-time")));

    const state = {
        gl, program, vao, vbo, ibo, mesh, uniforms, marks,
        yaw: -0.55, pitch: 0.72, sway: 0, drag: null, time: 0, unregister: null, observer: null,
        cleanup: [],
    };
    states.set(canvas, state);

    const draw = () => {
        const w = canvas.clientWidth, h = canvas.clientHeight;
        if (w <= 0 || h <= 0) return;
        const scale = fx.dpr();
        if (canvas.width !== Math.round(w * scale) || canvas.height !== Math.round(h * scale)) {
            canvas.width = Math.round(w * scale);
            canvas.height = Math.round(h * scale);
        }
        gl.viewport(0, 0, canvas.width, canvas.height);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);

        const dist = 3.1, yaw = state.yaw + state.sway;
        const eye = [
            Math.sin(yaw) * Math.cos(state.pitch) * dist,
            Math.sin(state.pitch) * dist,
            Math.cos(yaw) * Math.cos(state.pitch) * dist,
        ];
        const mvp = multiply(perspective(0.62, w / h, 0.1, 20), lookAt(eye, [0, mesh.peak * 0.25, 0]));

        gl.useProgram(program);
        gl.bindVertexArray(vao);
        gl.uniformMatrix4fv(uniforms.mvp, false, mvp);
        gl.uniform1f(uniforms.time, state.time);
        gl.drawElements(gl.TRIANGLES, mesh.indices.length, gl.UNSIGNED_INT, 0);

        for (const m of state.marks) {
            const [x, y] = project(mvp, m.pos);
            m.el.style.transform = `translate(${((x + 1) / 2) * w}px, ${((1 - y) / 2) * h}px) translate(-50%, -50%)`;
        }
    };
    state.draw = draw;

    // Dragging orbits the camera. User-driven, so it is allowed at every motion level.
    const onDown = (e) => {
        state.drag = { x: e.clientX, y: e.clientY, yaw: state.yaw, pitch: state.pitch };
        canvas.setPointerCapture(e.pointerId);
    };
    const onMove = (e) => {
        if (!state.drag) return;
        state.yaw = state.drag.yaw - (e.clientX - state.drag.x) * 0.008;
        state.pitch = Math.max(0.2, Math.min(1.35, state.drag.pitch + (e.clientY - state.drag.y) * 0.006));
        if (!state.unregister) requestAnimationFrame(draw);
    };
    const onUp = () => { state.drag = null; };
    canvas.addEventListener("pointerdown", onDown);
    canvas.addEventListener("pointermove", onMove);
    canvas.addEventListener("pointerup", onUp);
    canvas.addEventListener("pointercancel", onUp);
    state.cleanup.push(() => {
        canvas.removeEventListener("pointerdown", onDown);
        canvas.removeEventListener("pointermove", onMove);
        canvas.removeEventListener("pointerup", onUp);
        canvas.removeEventListener("pointercancel", onUp);
    });

    state.observer = new ResizeObserver(() => { if (!state.unregister) draw(); });
    state.observer.observe(canvas);

    // Full motion: the shimmer runs and the view sways slowly while nobody is dragging.
    // Otherwise it is a still picture that only moves under the user's own hand.
    // The sway is an offset on top of the dragged angle, so a drag never snaps back.
    if (fx.animates()) {
        state.unregister = fx.register("terrain", (dt) => {
            state.time += dt;
            state.sway = Math.sin(state.time * 0.25) * 0.18;
            draw();
        }, { fps: 30 });
    } else {
        draw();
    }

    return true;
}

/** Releases the GL context, the ticker and the listeners. Safe to call twice. */
export function destroy(canvas) {
    teardown(canvas, true);
}

function teardown(canvas, loseContext) {
    const state = canvas && states.get(canvas);
    if (!state) return;
    state.unregister?.();
    state.observer?.disconnect();
    state.cleanup.forEach((fn) => fn());
    const { gl } = state;
    gl.deleteBuffer(state.vbo);
    gl.deleteBuffer(state.ibo);
    gl.deleteVertexArray(state.vao);
    gl.deleteProgram(state.program);
    if (loseContext) gl.getExtension("WEBGL_lose_context")?.loseContext();
    states.delete(canvas);
}
