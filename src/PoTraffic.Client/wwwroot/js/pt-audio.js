// pt-audio.js — the app's whole sound design, synthesised. No audio files, ever.
//
// This replaces a set of bare oscillator beeps. Beeps are what an interface sounds
// like when nobody decided what it should sound like; the difference between a beep
// and a cue is that a cue is IN TUNE with the others, so hearing two in a row does
// not sound like a fault.
//
// THE RULES THIS FOLLOWS
//
//  * One key. Everything is built from A♭ major pentatonic (the black keys, in
//    effect) — a scale with no semitone clashes, so any two cues overlapping still
//    sound intentional rather than like an error.
//  * Meaning maps to direction. Good news rises, bad news falls. That is learnable
//    without anyone explaining it.
//  * Nothing is a pure sine. Every voice gets a detuned second oscillator and a
//    lowpass with its own envelope, which is the difference between "electronic
//    beep" and "instrument".
//  * Everything is short. The longest cue is 700ms. An app that sings at you is
//    an app people mute.
//  * One output bus, one master gain, one user volume. Muting is instant and total.
//
// Haptics ride alongside: the same call that plays a cue buzzes the phone, because a
// device on silent should still be able to confirm a tap.

import * as fx from "./pt-fx.js";

let ctx = null;
let master = null;      // user volume
let bus = null;         // compressor → destination, so stacked cues never clip
let reverb = null;      // shared send, gives every cue the same room
let reverbSend = null;
let unlocked = false;

// ── Scale ────────────────────────────────────────────────────────────────────

/**
 * A♭ major pentatonic across three octaves, in Hz. Cues pick degrees from this by
 * index, so "up a step" is always musical and never a semitone clash.
 */
const SCALE = (() => {
    const root = 207.65;                    // A♭3
    const steps = [0, 2, 4, 7, 9];          // major pentatonic, in semitones
    const notes = [];
    for (let octave = 0; octave < 4; octave++) {
        for (const s of steps) notes.push(root * Math.pow(2, (s + octave * 12) / 12));
    }
    return notes;
})();

const note = (degree) => SCALE[Math.max(0, Math.min(SCALE.length - 1, degree))];

// ── Graph ────────────────────────────────────────────────────────────────────

function ensure() {
    if (ctx) return ctx;

    const Ctor = window.AudioContext || window.webkitAudioContext;
    if (!Ctor) return null;

    ctx = new Ctor();

    // Compressor before the destination. Two cues landing together — a save
    // confirming while a poll completes — would otherwise clip audibly.
    bus = ctx.createDynamicsCompressor();
    bus.threshold.value = -18;
    bus.knee.value = 12;
    bus.ratio.value = 4;
    bus.attack.value = 0.003;
    bus.release.value = 0.18;

    master = ctx.createGain();
    master.gain.value = fx.soundEnabled() ? fx.volume() : 0;

    bus.connect(master).connect(ctx.destination);

    // A small synthetic room. Without it every cue sounds like it is happening
    // inside the speaker; with it they sound like they are happening in a space.
    reverb = ctx.createConvolver();
    reverb.buffer = impulse(1.4, 2.6);
    reverbSend = ctx.createGain();
    reverbSend.gain.value = 0.16;
    reverbSend.connect(reverb).connect(bus);

    fx.onChange(applyVolume);
    return ctx;
}

/** Exponentially-decaying noise — a serviceable small-room impulse response. */
function impulse(seconds, decay) {
    const length = Math.floor(ctx.sampleRate * seconds);
    const buffer = ctx.createBuffer(2, length, ctx.sampleRate);
    for (let channel = 0; channel < 2; channel++) {
        const data = buffer.getChannelData(channel);
        for (let i = 0; i < length; i++) {
            data[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / length, decay);
        }
    }
    return buffer;
}

function applyVolume() {
    if (!master || !ctx) return;
    const target = fx.soundEnabled() ? fx.volume() : 0;
    // Ramped, not assigned: a step change in gain is a click.
    master.gain.cancelScheduledValues(ctx.currentTime);
    master.gain.setTargetAtTime(target, ctx.currentTime, 0.02);
    if (target === 0) stopEngine();
}

/**
 * Unlocks the context. Browsers refuse to start audio without a user gesture, so
 * this must be called from a real click/tap handler.
 */
export function unlock() {
    if (unlocked) return;
    const c = ensure();
    if (!c) return;
    if (c.state === "suspended") c.resume();

    const source = c.createBufferSource();
    source.buffer = c.createBuffer(1, 1, 22050);
    source.connect(c.destination);
    source.start(0);
    unlocked = true;
}

// ── Voices ───────────────────────────────────────────────────────────────────

/**
 * One plucked note: two detuned saw/triangle oscillators through a lowpass whose
 * cutoff falls with the amplitude, which is what makes a synthesised note read as
 * "struck" rather than "switched on".
 */
function pluck({ freq, at = 0, dur = 0.32, gain = 0.16, type = "triangle", detune = 7, bright = 5 }) {
    const c = ensure();
    if (!c) return;

    const t = c.currentTime + at;

    const amp = c.createGain();
    const filter = c.createBiquadFilter();
    filter.type = "lowpass";
    filter.Q.value = 1.1;
    filter.frequency.setValueAtTime(freq * bright, t);
    filter.frequency.exponentialRampToValueAtTime(Math.max(freq * 0.9, 120), t + dur);

    for (const cents of [-detune, detune]) {
        const osc = c.createOscillator();
        osc.type = type;
        osc.frequency.setValueAtTime(freq, t);
        osc.detune.setValueAtTime(cents, t);
        osc.connect(filter);
        osc.start(t);
        osc.stop(t + dur + 0.05);
    }

    // 4ms attack: fast enough to feel instant, slow enough not to click.
    amp.gain.setValueAtTime(0, t);
    amp.gain.linearRampToValueAtTime(gain, t + 0.004);
    amp.gain.exponentialRampToValueAtTime(0.0001, t + dur);

    filter.connect(amp);
    amp.connect(bus);
    amp.connect(reverbSend);
}

/** Filtered noise — the percussive half of the palette (dismiss, delete, error). */
function hit({ at = 0, dur = 0.12, gain = 0.1, cutoff = 2400, sweepTo = 300, type = "lowpass" }) {
    const c = ensure();
    if (!c) return;

    const t = c.currentTime + at;
    const length = Math.max(1, Math.floor(c.sampleRate * dur));
    const buffer = c.createBuffer(1, length, c.sampleRate);
    const data = buffer.getChannelData(0);
    for (let i = 0; i < length; i++) data[i] = Math.random() * 2 - 1;

    const source = c.createBufferSource();
    source.buffer = buffer;

    const filter = c.createBiquadFilter();
    filter.type = type;
    filter.frequency.setValueAtTime(cutoff, t);
    filter.frequency.exponentialRampToValueAtTime(Math.max(sweepTo, 40), t + dur);

    const amp = c.createGain();
    amp.gain.setValueAtTime(gain, t);
    amp.gain.exponentialRampToValueAtTime(0.0001, t + dur);

    source.connect(filter).connect(amp);
    amp.connect(bus);
    source.start(t);
    source.stop(t + dur + 0.02);
}

/** A chord, spread slightly in time so it strums rather than blocks. */
function chord(degrees, { dur = 0.5, gain = 0.13, spread = 0.028, type = "triangle" } = {}) {
    degrees.forEach((d, i) => pluck({
        freq: note(d), at: i * spread, dur: dur - i * spread, gain, type,
    }));
}

// ── Haptics ──────────────────────────────────────────────────────────────────

/** Vibration patterns, in ms. Kept short — a long buzz feels like an error everywhere. */
const HAPTICS = {
    click: [8],
    save: [12, 40, 18],
    delete: [22],
    warn: [18, 60, 18],
    error: [30, 50, 30, 50, 30],
    success: [10, 30, 10, 30, 24],
    swipe: [6],
    probe: [10],
};

/**
 * Buzzes the device. Independent of the sound setting on purpose: a phone on silent
 * is the exact situation where haptics are the only confirmation available.
 * Suppressed under reduced motion, which some users set specifically to stop this.
 */
export function haptic(kind) {
    if (!fx.draws()) return;
    if (!navigator.vibrate) return;
    try { navigator.vibrate(HAPTICS[kind] ?? HAPTICS.click); } catch { /* blocked */ }
}

// ── Cues ─────────────────────────────────────────────────────────────────────

/**
 * Plays a named cue and fires the matching haptic.
 *
 * Degrees are indices into the pentatonic scale, so every interval below is
 * consonant by construction — there is no way to accidentally write a sour cue.
 */
export function play(kind) {
    haptic(kind);

    if (!unlocked || !fx.soundEnabled()) return;
    ensure();
    if (!ctx) return;

    switch (kind) {
        // Rising third — the universal "done, and it worked".
        case "save":
            chord([10, 12], { dur: 0.42, gain: 0.15 });
            break;

        // Full arpeggio up. Reserved for a genuinely good result, so it stays special.
        case "success":
            chord([10, 12, 14, 17], { dur: 0.62, gain: 0.13, spread: 0.045 });
            break;

        // A dry thud, pitched low. Deliberately not unpleasant: deleting is a normal
        // thing to do, and the undo bar is right there.
        case "delete":
            hit({ dur: 0.14, gain: 0.11, cutoff: 900, sweepTo: 120 });
            pluck({ freq: note(3), dur: 0.2, gain: 0.09, type: "sine" });
            break;

        // Falling second. Attention, not alarm.
        case "warn":
            pluck({ freq: note(12), dur: 0.2, gain: 0.13 });
            pluck({ freq: note(10), at: 0.13, dur: 0.34, gain: 0.13 });
            break;

        // Falling fourth plus noise. The only cue allowed to sound wrong.
        case "error":
            hit({ dur: 0.2, gain: 0.1, cutoff: 1800, sweepTo: 160 });
            pluck({ freq: note(9), dur: 0.26, gain: 0.14, type: "sawtooth", bright: 3 });
            pluck({ freq: note(5), at: 0.11, dur: 0.44, gain: 0.13, type: "sawtooth", bright: 3 });
            break;

        // Sub-100ms, quiet, high. Heard fifty times a session, so it has to disappear
        // into the background — anything with body becomes maddening at that rate.
        case "click":
            pluck({ freq: note(17), dur: 0.07, gain: 0.05, type: "sine", bright: 8 });
            break;

        case "swipe":
            hit({ dur: 0.09, gain: 0.05, cutoff: 5200, sweepTo: 1800, type: "bandpass" });
            break;

        // A probe going out. One note, low, felt more than heard.
        case "probe":
            pluck({ freq: note(7), dur: 0.16, gain: 0.07, type: "sine", bright: 4 });
            break;

        default:
            pluck({ freq: note(12), dur: 0.1, gain: 0.06 });
    }
}

/**
 * The result of a probe, sung. Rises for better than usual, falls for worse — the
 * chart says the same thing, but this arrives without needing to be looked at.
 */
export function verdict(level) {
    switch (level) {
        case "clear": play("success"); break;
        case "normal": play("save"); break;
        case "slow": play("warn"); break;
        case "heavy": play("error"); break;
        default: play("probe");
    }
}

// ── Ambient engine (#10) ─────────────────────────────────────────────────────
//
// A very quiet drone while a probe is in flight. Its pitch and roughness rise with
// the traffic mood, so a jam is audible as tension before any number has updated.
// It is a DRONE, not a loop: two detuned oscillators and a filter, running until
// told to stop. Nothing is scheduled, so it costs no frames at all.

let engine = null;

export function startEngine() {
    if (!fx.soundEnabled() || !unlocked) return;
    ensure();
    if (!ctx || engine) return;

    const t = ctx.currentTime;
    const amp = ctx.createGain();
    amp.gain.setValueAtTime(0, t);
    amp.gain.linearRampToValueAtTime(0.045, t + 0.7);   // fade in; a drone that starts abruptly startles

    const filter = ctx.createBiquadFilter();
    filter.type = "lowpass";
    filter.frequency.setValueAtTime(420, t);
    filter.Q.value = 3.2;

    // A slow LFO on the filter keeps the drone from sounding like a dial tone.
    const lfo = ctx.createOscillator();
    const lfoGain = ctx.createGain();
    lfo.frequency.value = 0.22;
    lfoGain.gain.value = 120;
    lfo.connect(lfoGain).connect(filter.frequency);
    lfo.start(t);

    const oscillators = [-9, 9].map((cents) => {
        const osc = ctx.createOscillator();
        osc.type = "sawtooth";
        osc.frequency.setValueAtTime(note(0), t);
        osc.detune.setValueAtTime(cents, t);
        osc.connect(filter);
        osc.start(t);
        return osc;
    });

    filter.connect(amp).connect(bus);

    engine = { amp, filter, lfo, lfoGain, oscillators };
    tuneEngine();
}

/** Re-pitches the running drone to the current mood. Safe to call when silent. */
export function tuneEngine() {
    if (!engine || !ctx) return;
    const mood = fx.getMood();
    const t = ctx.currentTime;

    // Up a fifth and brighter as the commute jams — rising tension, not rising volume.
    engine.oscillators.forEach((osc) =>
        osc.frequency.setTargetAtTime(note(0) * (1 + mood * 0.5), t, 1.5));
    engine.filter.frequency.setTargetAtTime(320 + mood * 900, t, 1.5);
    engine.lfoGain.gain.setTargetAtTime(80 + mood * 260, t, 1.5);
    engine.lfo.frequency.setTargetAtTime(0.18 + mood * 0.9, t, 1.5);
}

export function stopEngine() {
    if (!engine || !ctx) return;

    const t = ctx.currentTime;
    engine.amp.gain.cancelScheduledValues(t);
    engine.amp.gain.setTargetAtTime(0, t, 0.25);

    const dying = engine;
    engine = null;
    // Stop the oscillators only after the fade has finished, or the tail is a click.
    setTimeout(() => {
        try {
            dying.oscillators.forEach((o) => o.stop());
            dying.lfo.stop();
        } catch { /* already stopped */ }
    }, 1400);
}

/** Whether the drone is currently running — lets .NET avoid redundant calls. */
export function engineRunning() {
    return engine !== null;
}
