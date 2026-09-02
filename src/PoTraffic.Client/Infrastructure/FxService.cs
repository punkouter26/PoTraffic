using Microsoft.JSInterop;

namespace PoTraffic.Client.Infrastructure;

/// <summary>How much decorative motion the app is allowed to draw.</summary>
public enum MotionLevel
{
    /// <summary>Everything: the map's traffic flow animates and charts draw themselves in.</summary>
    Full,

    /// <summary>Effects still render, but nothing moves. Colour keeps its meaning; motion stops.</summary>
    Reduced,

    /// <summary>No decorative rendering at all.</summary>
    Off,
}

/// <summary>
/// The app's motion setting, and the bridge to <c>js/pt-fx.js</c> that owns it at runtime.
///
/// <para>
/// This used to carry a WebGL background wash, a particle burst and a synthesised cue set
/// with volume control — roughly 40KB of JavaScript and four interop modules, on an app
/// whose job is telling you when to leave the house. They were removed; what remains is
/// the one setting that still governs something a user can see, the traffic flow on the
/// map and the chart draw-in.
/// </para>
///
/// <para>
/// The preference lives in <c>localStorage</c> on the JS side rather than in this class,
/// deliberately: <c>pt-fx.js</c> is imported before Blazor has finished booting, and an
/// effect that waits for .NET to tell it whether it may animate is one that flickers on at
/// second three of every page load.
/// </para>
///
/// <para>
/// The OS <c>prefers-reduced-motion</c> setting is a floor, not a default — see the note in
/// <c>pt-fx.js</c>. Someone who asked their system for less motion does not get full motion
/// back because this app's setting says "full".
/// </para>
/// </summary>
public sealed class FxService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private IJSObjectReference? _fx;
    private bool _started;

    public FxService(IJSRuntime js) => _js = js;

    /// <summary>Raised after the setting changes, so open pages can re-render their controls.</summary>
    public event Action? StateChanged;

    public MotionLevel Motion { get; private set; } = MotionLevel.Full;

    /// <summary>
    /// Loads the effects module and reads the stored preference. Called once from the root
    /// layout; safe to call again.
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
        }
        catch (JSDisconnectedException) { /* page tearing down */ }
        catch (ObjectDisposedException) { /* page tearing down */ }
        catch (JSException)
        {
            // A blocked module or absent localStorage is not worth surfacing. The app
            // renders exactly as it did before the effects existed.
        }
    }

    private Task<IJSObjectReference> Import(string path) =>
        _js.InvokeAsync<IJSObjectReference>("import", path).AsTask();

    public async Task SetMotionAsync(MotionLevel level)
    {
        Motion = level;
        await Safe(() => _fx!.InvokeVoidAsync("setMotionLevel", Serialize(level)).AsTask());
        StateChanged?.Invoke();
    }

    private async Task ReadAsync()
    {
        if (_fx is null)
            return;

        Motion = Parse(await _fx.InvokeAsync<string>("motionLevel"));
    }

    /// <summary>
    /// Every effect call goes through here. Decoration must never be able to break a page:
    /// a missing module or a torn-down circuit both end the same way, which is quietly.
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
        if (_fx is null)
            return;

        try { await _fx.DisposeAsync(); }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
        catch (JSException) { }
    }
}
