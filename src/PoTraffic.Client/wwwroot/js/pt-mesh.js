// pt-mesh.js — lightweight WebGL mesh gradient background.
// Renders an animated aurora-like gradient on a single full-viewport canvas.
// Cheap (≈0.3ms/frame on modern phones), respects prefers-reduced-motion,
// pauses when the tab is hidden.

const vsSource = `
attribute vec2 a_pos;
varying vec2 v_uv;
void main() {
    v_uv = (a_pos + 1.0) * 0.5;
    gl_Position = vec4(a_pos, 0.0, 1.0);
}`;

const fsSource = `
precision mediump float;
varying vec2 v_uv;
uniform float u_time;
uniform vec2 u_resolution;
uniform float u_reduced;

// Cheap hash + noise — generates a soft 3-blob mesh gradient.
float hash(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}
float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash(i);
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

void main() {
    vec2 uv = v_uv;
    float t = u_time * 0.00012 * (1.0 - u_reduced);

    // Three animated centers — derived from time + UV
    vec2 c1 = vec2(0.30 + 0.10 * sin(t * 1.7), 0.30 + 0.10 * cos(t * 1.3));
    vec2 c2 = vec2(0.70 + 0.12 * cos(t * 1.1), 0.65 + 0.10 * sin(t * 1.9));
    vec2 c3 = vec2(0.50 + 0.15 * sin(t * 0.9), 0.85 + 0.08 * cos(t * 1.4));

    // Soft falloff from each center
    float d1 = distance(uv, c1);
    float d2 = distance(uv, c2);
    float d3 = distance(uv, c3);
    float i1 = exp(-d1 * 4.0);
    float i2 = exp(-d2 * 5.0);
    float i3 = exp(-d3 * 6.0);

    // Soft additive blend
    vec3 col = vec3(0.95, 0.97, 1.00);                 // base slate
    col += vec3(0.23, 0.51, 0.96) * i1 * 0.18;        // brand blue
    col += vec3(0.39, 0.40, 0.95) * i2 * 0.12;        // indigo
    col += vec3(0.06, 0.72, 0.51) * i3 * 0.10;        // emerald accent

    // Subtle film grain via hash
    float g = hash(uv * u_resolution.xy + u_time) - 0.5;
    col += g * 0.012;

    gl_FragColor = vec4(col, 1.0);
}`;

let gl = null, program = null, buffer = null, uTime = null, uRes = null, uRed = null;
let startTime = 0, rafId = 0, canvas = null;
let reduceMotion = false, hidden = false;

function compile(type, src) {
    const s = gl.createShader(type);
    gl.shaderSource(s, src);
    gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) {
        console.error("pt-mesh: shader compile failed", gl.getShaderInfoLog(s));
        gl.deleteShader(s);
        return null;
    }
    return s;
}

function init(canvasEl) {
    canvas = canvasEl;
    gl = canvas.getContext("webgl", { antialias: false, alpha: false, premultipliedAlpha: false });
    if (!gl) {
        canvas.style.display = "none";
        return false;
    }
    const vs = compile(gl.VERTEX_SHADER, vsSource);
    const fs = compile(gl.FRAGMENT_SHADER, fsSource);
    program = gl.createProgram();
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        console.error("pt-mesh: program link failed", gl.getProgramInfoLog(program));
        return false;
    }
    buffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1,  1, -1, -1,  1,  -1,  1,  1, -1,  1,  1]), gl.STATIC_DRAW);
    const aPos = gl.getAttribLocation(program, "a_pos");
    gl.enableVertexAttribArray(aPos);
    gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, 0, 0);
    uTime = gl.getUniformLocation(program, "u_time");
    uRes = gl.getUniformLocation(program, "u_resolution");
    uRed = gl.getUniformLocation(program, "u_reduced");
    gl.useProgram(program);
    startTime = performance.now();
    reduceMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;
    return true;
}

function resize() {
    if (!canvas) return;
    const dpr = Math.min(window.devicePixelRatio || 1, 1.5);
    canvas.width = Math.floor(canvas.clientWidth * dpr);
    canvas.height = Math.floor(canvas.clientHeight * dpr);
    gl.viewport(0, 0, canvas.width, canvas.height);
    gl.uniform2f(uRes, canvas.width, canvas.height);
}

function frame(now) {
    if (hidden) { rafId = requestAnimationFrame(frame); return; }
    gl.uniform1f(uTime, now - startTime);
    gl.uniform1f(uRed, reduceMotion ? 1.0 : 0.0);
    gl.drawArrays(gl.TRIANGLES, 0, 6);
    rafId = requestAnimationFrame(frame);
}

export function start(canvasEl) {
    if (!canvasEl) return;
    if (!init(canvasEl)) return;
    resize();
    window.addEventListener("resize", resize, { passive: true });
    document.addEventListener("visibilitychange", () => { hidden = document.hidden; });
    rafId = requestAnimationFrame(frame);
}

export function stop() {
    if (rafId) cancelAnimationFrame(rafId);
    rafId = 0;
}