using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace PoTraffic.Client.Infrastructure;

/// <summary>
/// Thin wrapper around the pt-audio.js / pt-gestures.js JS modules.
/// Lazy-loads each module on first use; safe to no-op if the module is missing.
/// </summary>
public sealed class PtInterop : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private Task<IJSObjectReference>? _audio;
    private Task<IJSObjectReference>? _gestures;

    public PtInterop(IJSRuntime js) => _js = js;

    private Task<IJSObjectReference> Audio() => _audio ??= _js.InvokeAsync<IJSObjectReference>(
        "import", "./js/pt-audio.js").AsTask();

    private Task<IJSObjectReference> Gestures() => _gestures ??= _js.InvokeAsync<IJSObjectReference>(
        "import", "./js/pt-gestures.js").AsTask();

    /// <summary>Unlock the audio context after a user gesture.</summary>
    public async Task UnlockAudioAsync()
    {
        try { await (await Audio()).InvokeVoidAsync("unlock"); }
        catch { /* JS not ready yet */ }
    }

    /// <summary>Play a feedback cue. Silent no-op if context is not yet unlocked.</summary>
    public async Task PlayAsync(string kind)
    {
        try { await (await Audio()).InvokeVoidAsync("play", kind); }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Attach swipe/long-press gesture handlers to <paramref name="element"/>.
    /// Calls <paramref name="callbackMethodName"/> on the supplied DotNetObjectReference
    /// with a string argument: "left" | "right" | "longpress".
    /// </summary>
    public async ValueTask<IAsyncDisposable?> AttachSwipeAsync(
        ElementReference element,
        object dotnetRef,
        string callbackMethodName)
    {
        try
        {
            var module = await Gestures();
            // JS returns a cleanup function. We wrap it as an Action so disposal
            // can simply invoke it back through the module.
            var cleanupRef = await module.InvokeAsync<IJSObjectReference>(
                "attachSwipe", element, dotnetRef, callbackMethodName);
            return new DisposableWrapper(module, cleanupRef);
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_audio is { IsCompletedSuccessfully: true } a)
            try { await (await a).DisposeAsync(); } catch { }
        if (_gestures is { IsCompletedSuccessfully: true } g)
            try { await (await g).DisposeAsync(); } catch { }
    }

    private sealed class DisposableWrapper : IAsyncDisposable
    {
        private readonly IJSObjectReference _module;
        private readonly IJSObjectReference _cleanup;
        public DisposableWrapper(IJSObjectReference module, IJSObjectReference cleanup)
        {
            _module = module;
            _cleanup = cleanup;
        }
        public async ValueTask DisposeAsync()
        {
            try { await _cleanup.InvokeVoidAsync("call", null); }
            catch { /* swallow */ }
            try { await _cleanup.DisposeAsync(); } catch { /* swallow */ }
        }
    }
}