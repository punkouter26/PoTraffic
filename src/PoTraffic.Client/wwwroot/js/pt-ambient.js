// pt-ambient.js — the living gradient behind the whole app.
//
// A full-viewport WebGL2 quad running a domain-warped value-noise field, tinted by
// the app-wide traffic mood: cool and slow when the commute is clear, hot and
// agitated when it is jammed. It sits at z-index -1 behind everything and never
// takes pointer events, so it is decoration in the strictest sense — remove it and
// the app is unchanged.
//
// COST CONTROL, because a fullscreen fragment shader is the easiest way to burn a
// phone battery:
//   * It renders at 30fps, not 60. The wash takes ~40 seconds to visibly change;
//     half the frames are indistinguishable and cost half the GPU.
//   * The backing buffer is capped at 0.5× device pixels and upscaled by the
//     compositor. Noise is low-frequency — nobody can see the difference, and it
//     cuts fill rate to a quarter.
//   * Two octaves of noise, not five. The third octave is invisible under a 40%
//     opacity wash.
//   * It unregisters entirely when the tab is hidden (via pt-fx's ticker).
//
// With reduced motion it paints ONE static frame and stops: the colour still carries
// the meaning, nothing moves.

import * as fx from "./pt-fx.js";

const VERT = `#version 300 es
in vec2 a_pos;
void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }`;

// Value noise + fBm + domain warp. Deliberately hash-based rather than texture-based:
// no asset to download, no upload, and it costs a handful of ALU ops per pixel.
const FRAG = `#version 300 es
precision mediump float;

uniform vec2  u_res;
uniform float u_time;
uniform float u_mood;      // 0 = clear, 1 = jammed
uniform vec3  u_cool;      // colour at mood 0
uniform vec3  u_hot;       // colour at mood 1
uniform vec3  u_page;      // the page background, so the wash blends into it
uniform float u_strength;  // overall opacity of the effect

out vec4 fragColor;

float hash(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    // Quintic smoothstep — the cubic one leaves visible grid creases at this scale.
    vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    return mix(
        mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x),
        mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x),
        u.y);
}

float fbm(vec2 p) {
    return noise(p) * 0.62 + noise(p * 2.17) * 0.31;
}

void main() {
    // Aspect-corrected so the blobs stay round on a wide monitor.
    vec2 uv = gl_FragCoord.xy / u_res;
    vec2 p = uv * vec2(u_res.x / u_res.y, 1.0);

    // A jam should feel restless: both the drift speed and the warp amplitude rise
    // with mood, so "bad" reads as agitation and not merely as a redder screen.
    float speed = mix(0.012, 0.055, u_mood);
    float t = u_time * speed;

    vec2 warp = vec2(
        fbm(p * 1.6 + vec2(t, t * 0.7)),
        fbm(p * 1.6 + vec2(-t * 0.8, t * 1.3)));

    float field = fbm(p * 2.1 + warp * mix(0.6, 1.8, u_mood) + vec2(t * 0.5, -t * 0.3));

    // Push the midtones apart so the field reads as distinct soft blobs rather than
    // an even grey fog.
    field = smoothstep(0.28, 0.78, field);

    vec3 tint = mix(u_cool, u_hot, u_mood);
    vec3 colour = mix(u_page, tint, field * u_strength);

    // Vignette: heaviest at the edges, absent behind the centre column where the
    // actual content lives, so text always sits on a calm ground.
    float centre = distance(uv, vec2(0.5));
    float vignette = smoothstep(0.15, 0.85, centre);
    float alpha = u_strength * mix(0.35, 1.0, vignette);

    fragColor = vec4(colour * alpha, alpha);
}`;

let state = null;

/** Rendering scale. Noise this soft survives being drawn at half resolution. */
const SCALE = 0.5;

function readPalette(host) {
    return {
        cool: fx.resolveRgb(host, "var(--pt-ambient-cool)", [0.13, 0.42, 0.85]),
        hot: fx.resolveRgb(host, "var(--pt-ambient-hot)", [0.85, 0.22, 0.25]),
        page: fx.resolveRgb(host, "var(--pt-bg-page)", [0.97, 0.98, 0.99]),
    };
}

function resize() {
    if (!state) return;
    const { canvas, gl } = state;
    const scale = fx.dpr() * SCALE;
    const w = Math.max(1, Math.round(window.innerWidth * scale));
    const h = Math.max(1, Math.round(window.innerHeight * scale));
    if (canvas.width === w && canvas.height === h) return;
    canvas.width = w;
    canvas.height = h;
    gl.viewport(0, 0, w, h);
}

function drawFrame(elapsed) {
    const { gl, program, uniforms, palette } = state;

    resize();

    gl.useProgram(program);
    gl.uniform2f(uniforms.res, state.canvas.width, state.canvas.height);
    gl.uniform1f(uniforms.time, elapsed);
    gl.uniform1f(uniforms.mood, state.mood);
    gl.uniform3fv(uniforms.cool, palette.cool);
    gl.uniform3fv(uniforms.hot, palette.hot);
    gl.uniform3fv(uniforms.page, palette.page);
    gl.uniform1f(uniforms.strength, state.strength);

    gl.drawArrays(gl.TRIANGLES, 0, 3);
}

/**
 * Mounts the background. Idempotent — calling it again just refreshes the palette,
 * which is exactly what a theme switch needs.
 */
export function start() {
    if (!fx.draws()) {
        stop();
        return;
    }

    if (state) {
        state.palette = readPalette(state.canvas.parentElement);
        if (!fx.animates()) drawFrame(state.elapsed);
        return;
    }

    const canvas = document.createElement("canvas");
    canvas.className = "pt-ambient";
    canvas.setAttribute("aria-hidden", "true");

    const gl = fx.gl2(canvas);
    if (!gl) return;   // No WebGL2: the page keeps its flat background. Nothing to report.

    const program = fx.buildProgram(gl, VERT, FRAG);
    if (!program) return;

    // One oversized triangle rather than two quad triangles: no diagonal seam, one
    // fewer vertex, and the classic way to run a fullscreen fragment pass.
    const buffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);

    const loc = gl.getAttribLocation(program, "a_pos");
    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);

    gl.enable(gl.BLEND);
    gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);   // premultiplied

    document.body.insertBefore(canvas, document.body.firstChild);

    state = {
        canvas, gl, program, vao,
        uniforms: {
            res: gl.getUniformLocation(program, "u_res"),
            time: gl.getUniformLocation(program, "u_time"),
            mood: gl.getUniformLocation(program, "u_mood"),
            cool: gl.getUniformLocation(program, "u_cool"),
            hot: gl.getUniformLocation(program, "u_hot"),
            page: gl.getUniformLocation(program, "u_page"),
            strength: gl.getUniformLocation(program, "u_strength"),
        },
        palette: readPalette(canvas.parentElement),
        elapsed: 0,
        mood: fx.getMood(),
        targetMood: fx.getMood(),
        strength: 0,
        unregister: null,
    };

    window.addEventListener("resize", onResize, { passive: true });

    if (fx.animates()) {
        // 30fps: this is a wash that takes half a minute to change. Sixty frames a
        // second of it is heat, not motion.
        state.unregister = fx.register("ambient", step, { fps: 30 });
    } else {
        state.strength = targetStrength();
        drawFrame(0);
    }

    fx.onChange(onPreferencesChanged);
}

function targetStrength() {
    // Stronger when things are bad, but never enough to fight the text on top of it.
    return fx.isDegraded() ? 0.16 : 0.22 + fx.getMood() * 0.16;
}

function step(dt) {
    if (!state) return;

    state.elapsed += dt;

    // Mood and strength are eased rather than snapped. A commute going from "normal"
    // to "heavy" should feel like the room changing temperature, not like a light
    // switch — and an abrupt full-screen colour change is exactly the kind of thing
    // that makes people describe an interface as "flashing".
    state.targetMood = fx.getMood();
    state.mood += (state.targetMood - state.mood) * Math.min(1, dt * 0.6);
    state.strength += (targetStrength() - state.strength) * Math.min(1, dt * 1.2);

    drawFrame(state.elapsed);
}

function onResize() {
    if (state && !fx.animates()) drawFrame(state.elapsed);
}

function onPreferencesChanged() {
    if (!state) {
        if (fx.draws()) start();
        return;
    }
    if (!fx.draws()) { stop(); return; }

    if (fx.animates() && !state.unregister) {
        state.unregister = fx.register("ambient", step, { fps: 30 });
    } else if (!fx.animates() && state.unregister) {
        state.unregister();
        state.unregister = null;
        state.mood = fx.getMood();
        state.strength = targetStrength();
        drawFrame(state.elapsed);
    }
}

/** Refreshes the palette after a theme change — the tokens resolve differently now. */
export function refreshTheme() {
    if (!state) return;
    state.palette = readPalette(state.canvas.parentElement);
    if (!fx.animates()) drawFrame(state.elapsed);
}

export function stop() {
    if (!state) return;
    state.unregister?.();
    window.removeEventListener("resize", onResize);
    state.canvas.remove();
    // Free the GPU memory now rather than waiting for the context to be collected.
    state.gl.getExtension("WEBGL_lose_context")?.loseContext();
    state = null;
}
