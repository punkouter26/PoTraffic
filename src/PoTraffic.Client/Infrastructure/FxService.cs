using Microsoft.JSInterop;

namespace PoTraffic.Client.Infrastructure;

/// <summary>How much decorative motion the app is allowed to draw.</summary>
public enum MotionLevel
{
    /// <summary>Everything: the background wash drifts, particles fly, charts draw themselves in.</summary>
    Full,

    /// <summary>Effects still render, but nothing moves. Colour keeps its meaning; motion stops.</summary>
    Reduced,

    /// <summary>No decorative rendering at all. The app is exactly what it was before any of this.</summary>
    Off,
}

/// <summary>
/// The app's effects and sound settings, and the bridge to <c>js/pt-fx.js</c> that owns
/// them at runtime.
///
/// <para>
/// The preferences live in <c>localStorage</c> on the JS side rather than in this class,
/// deliberately: <c>pt-fx.js</c> is imported by the background wash before Blazor has
/// finished booting, and an effect that has to wait for .NET to tell it whether it may
/// animate is an effect that flickers on at second three of every page load.
/// </para>
///
/// <para>
/// The OS <c>prefers-reduced-motion</c> setting is a floor, not a default — see the
/// note in <c>pt-fx.js</c>. Someone who asked their system for less motion does not get
/// full motion back because this app's setting says "full".
/// </para>
/// </summary>
public sealed class FxService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private IJSObjectReference? _fx;
    private IJSObjectReference? _ambient;
    private IJSObjectReference? _particles;
    private IJSObjectReference? _audio;
    private bool _started;

    public FxService(IJSRuntime js) => _js = js;

    /// <summary>Raised after a setting changes, so open pages can re-render their controls.</summary>
    public event Action? StateChanged;

    public MotionLevel Motion { get; private set; } = MotionLevel.Full;
    public bool SoundEnabled { get; private set; } = true;
    public double Volume { get; private set; } = 0.6;

    /// <summary>
    /// Loads the effect modules and starts the background wash. Called once from the
    /// root layout; safe to call again.
    /// </summary>
    public async Task StartAsync()
    {
        if (_started)
            return;
        _started = true;

        try
        {
            _fx = await Import("./js/pt-fx.js");
            await ReadAsync();

            _ambient = await Import("./js/pt-ambient.js");
            await _ambient.InvokeVoidAsync("start");
        }
        catch (JSDisconnectedException) { /* page tearing down */ }
        catch (ObjectDisposedException) { /* page tearing down */ }
        catch (JSException)
        {
            // No WebGL, no localStorage, a blocked module — none of it is worth
            // surfacing. The app renders exactly as it did before the effects existed.
        }
    }

    private Task<IJSObjectReference> Import(string path) =>
        _js.InvokeAsync<IJSObjectReference>("import", path).AsTask();

    // ── Settings ─────────────────────────────────────────────────────────────

    public async Task SetMotionAsync(MotionLevel level)
    {
        Motion = level;
        await Safe(async () =>
        {
            await _fx!.InvokeVoidAsync("setMotionLevel", Serialize(level));
            // The wash owns a GL context; it has to be told to tear down or spin up
            // rather than merely being told the level changed.
            if (_ambient is not null)
                await _ambient.InvokeVoidAsync(level == MotionLevel.Off ? "stop" : "start");
        });
        StateChanged?.Invoke();
    }

    public async Task SetSoundAsync(bool enabled)
    {
        SoundEnabled = enabled;
        await Safe(() => _fx!.InvokeVoidAsync("setSoundEnabled", enabled).AsTask());
        StateChanged?.Invoke();
    }

    public async Task SetVolumeAsync(double volume)
    {
        Volume = Math.Clamp(volume, 0, 1);
        await Safe(() => _fx!.InvokeVoidAsync("setVolume", Volume).AsTask());
        StateChanged?.Invoke();
    }

    /// <summary>Re-resolves the effect palettes after a light/dark switch.</summary>
    public async Task RefreshThemeAsync() =>
        await Safe(() => _ambient!.InvokeVoidAsync("refreshTheme").AsTask());

    // ── Effects ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires the celebration burst. Silently does nothing under reduced motion — the
    /// result it is celebrating is always also stated in text.
    /// </summary>
    public async Task CelebrateAsync(int count = 70)
    {
        if (Motion != MotionLevel.Full)
            return;

        await Safe(async () =>
        {
            _particles ??= await Import("./js/pt-particles.js");
            await _particles.InvokeVoidAsync("burst", null, count);
        });
    }

    /// <summary>Plays a named cue and fires the matching haptic. See <c>play()</c> in pt-audio.js.</summary>
    public async Task PlayAsync(string cue) =>
        await Safe(async () =>
        {
            _audio ??= await Import("./js/pt-audio.js");
            await _audio.InvokeVoidAsync("play", cue);
        });

    /// <summary>Sings a probe result: rising for better than usual, falling for worse.</summary>
    public async Task VerdictAsync(string level) =>
        await Safe(async () =>
        {
            _audio ??= await Import("./js/pt-audio.js");
            await _audio.InvokeVoidAsync("verdict", level);
        });

    /// <summary>Starts or stops the ambient drone that runs while a probe is in flight.</summary>
    public async Task SetEngineAsync(bool running) =>
        await Safe(async () =>
        {
            _audio ??= await Import("./js/pt-audio.js");
            await _audio.InvokeVoidAsync(running ? "startEngine" : "stopEngine");
        });

    /// <summary>Unlocks audio. Must be called from a real user gesture or browsers refuse.</summary>
    public async Task UnlockAudioAsync() =>
        await Safe(async () =>
        {
            _audio ??= await Import("./js/pt-audio.js");
            await _audio.InvokeVoidAsync("unlock");
        });

    /// <summary>Sets the app-wide traffic mood that the wash, the map flow and the drone all read.</summary>
    public async Task SetMoodAsync(string level) =>
        await Safe(async () =>
        {
            await _fx!.InvokeVoidAsync("setMood", level);
            if (_audio is not null)
                await _audio.InvokeVoidAsync("tuneEngine");
        });

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private async Task ReadAsync()
    {
        if (_fx is null)
            return;

        Motion = Parse(await _fx.InvokeAsync<string>("motionLevel"));
        SoundEnabled = await _fx.InvokeAsync<bool>("soundEnabled");
        Volume = await _fx.InvokeAsync<double>("volume");
    }

    /// <summary>
    /// Every effect call goes through here. Decoration must never be able to break a
    /// page: a missing module, a lost GL context or a torn-down circuit all end the
    /// same way, which is quietly.
    /// </summary>
    private static async Task Safe(Func<Task> action)
    {
        try { await action(); }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
        catch (JSException) { }
        catch (NullReferenceException) { /* StartAsync never completed */ }
    }

    private static string Serialize(MotionLevel level) => level switch
    {
        MotionLevel.Reduced => "reduced",
        MotionLevel.Off => "off",
        _ => "full",
    };

    private static MotionLevel Parse(string? value) => value switch
    {
        "reduced" => MotionLevel.Reduced,
        "off" => MotionLevel.Off,
        _ => MotionLevel.Full,
    };

    public async ValueTask DisposeAsync()
    {
        foreach (IJSObjectReference? module in new[] { _ambient, _particles, _audio, _fx })
        {
            if (module is null)
                continue;
            try { await module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
            catch (ObjectDisposedException) { }
            catch (JSException) { }
        }
    }
}
