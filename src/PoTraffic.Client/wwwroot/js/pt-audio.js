// pt-audio.js — micro-feedback via Web Audio API. No asset downloads.
// AudioContext is unlocked on the first user gesture (autoplay policy).

let ctx = null;
let unlocked = false;

function ensure() {
    if (ctx) return ctx;
    const Ctor = window.AudioContext || window.webkitAudioContext;
    if (!Ctor) return null;
    ctx = new Ctor();
    return ctx;
}

export function unlock() {
    if (unlocked) return;
    const c = ensure();
    if (!c) return;
    if (c.state === "suspended") c.resume();
    // play a single 1ms silent buffer to satisfy unlock heuristics
    const buf = c.createBuffer(1, 1, 22050);
    const src = c.createBufferSource();
    src.buffer = buf;
    src.connect(c.destination);
    src.start(0);
    unlocked = true;
}

// Lightweight envelope helpers — all zero-asset, all < 200ms total.
function envBeep({ freq = 880, type = "sine", dur = 0.12, gain = 0.06, freqEnd }) {
    const c = ensure();
    if (!c) return;
    const o = c.createOscillator();
    const g = c.createGain();
    o.type = type;
    o.frequency.setValueAtTime(freq, c.currentTime);
    if (freqEnd) o.frequency.exponentialRampToValueAtTime(freqEnd, c.currentTime + dur);
    g.gain.setValueAtTime(0, c.currentTime);
    g.gain.linearRampToValueAtTime(gain, c.currentTime + 0.005);
    g.gain.exponentialRampToValueAtTime(0.0001, c.currentTime + dur);
    o.connect(g).connect(c.destination);
    o.start();
    o.stop(c.currentTime + dur);
}

function noiseBurst({ dur = 0.06, gain = 0.04 }) {
    const c = ensure();
    if (!c) return;
    const buf = c.createBuffer(1, c.sampleRate * dur, c.sampleRate);
    const data = buf.getChannelData(0);
    for (let i = 0; i < data.length; i++) data[i] = (Math.random() * 2 - 1) * (1 - i / data.length);
    const src = c.createBufferSource();
    const g = c.createGain();
    g.gain.value = gain;
    src.buffer = buf;
    src.connect(g).connect(c.destination);
    src.start();
}

export function play(kind) {
    if (!unlocked) return; // require a user gesture first
    switch (kind) {
        case "save":      envBeep({ freq: 880, freqEnd: 1320, type: "triangle", dur: 0.14 }); break;
        case "delete":    noiseBurst({ dur: 0.08, gain: 0.05 }); break;
        case "warn":      envBeep({ freq: 440, type: "square", dur: 0.16, gain: 0.04 }); break;
        case "click":     envBeep({ freq: 660, type: "sine", dur: 0.05, gain: 0.03 }); break;
        case "error":     noiseBurst({ dur: 0.18, gain: 0.06 }); envBeep({ freq: 220, type: "sawtooth", dur: 0.2, gain: 0.04 }); break;
        default:          envBeep({ freq: 880, dur: 0.05, gain: 0.03 });
    }
}