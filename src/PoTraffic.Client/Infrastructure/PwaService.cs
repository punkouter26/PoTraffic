using Microsoft.JSInterop;

namespace PoTraffic.Client.Infrastructure;

/// <summary>
/// Owns the service-worker registration and the browser's install prompt.
///
/// <para>
/// A service rather than a component because the two consumers sit in different
/// places: the update bar lives in the root layout so it survives navigation, while
/// the install button lives in Settings. Both need the same live state, and the
/// underlying <c>beforeinstallprompt</c> event fires exactly once per page load —
/// long before Settings is likely to be on screen — so something longer-lived than
/// either component has to hold it.
/// </para>
/// </summary>
public sealed class PwaService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private DotNetObjectReference<PwaService>? _self;
    private bool _started;
    private bool _attached;

    public PwaService(IJSRuntime js) => _js = js;

    /// <summary>Raised when the browser's install or update state changes.</summary>
    public event Action? StateChanged;

    /// <summary>True when the browser is willing to show its install dialog right now.</summary>
    public bool CanInstall { get; private set; }

    /// <summary>True when a newer build has been downloaded and is waiting to take over.</summary>
    public bool UpdateReady { get; private set; }

    /// <summary>True when the page is already running as an installed app.</summary>
    public bool Installed { get; private set; }

    /// <summary>False on browsers with no service-worker support — the UI hides itself rather than lying.</summary>
    public bool Supported { get; private set; } = true;

    /// <summary>
    /// Subscribes to the browser state that <c>js/pt-pwa.js</c> has been collecting since
    /// the document was parsed. Idempotent: called from the root layout on first render,
    /// and harmless if something else calls it again.
    ///
    /// <para>Registration itself already happened in that script — it has to, because the
    /// install event fires before Blazor finishes booting.</para>
    /// </summary>
    public async Task StartAsync()
    {
        if (_started)
            return;
        _started = true;

        try
        {
            _self = DotNetObjectReference.Create(this);
            await _js.InvokeVoidAsync("ptPwa.attach", _self);
            _attached = true;

            await RefreshAsync();
        }
        catch (JSDisconnectedException) { /* page tearing down */ }
        catch (ObjectDisposedException) { /* page tearing down */ }
        catch (JSException)
        {
            // The script failed to load, or this browser has no service worker at all
            // (private mode, insecure origin). Everything else in the app is unaffected.
            Supported = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Called from pt-pwa.js whenever the browser's install or update state moves.</summary>
    [JSInvokable]
    public async Task OnPwaStateChanged() => await RefreshAsync();

    /// <summary>Shows the browser's install dialog. Returns true when the user accepted.</summary>
    public async Task<bool> InstallAsync()
    {
        if (!_attached)
            return false;

        try
        {
            return await _js.InvokeAsync<bool>("ptPwa.promptInstall");
        }
        catch (JSException) { return false; }
        catch (JSDisconnectedException) { return false; }
    }

    /// <summary>
    /// Hands control to the waiting build. The page reloads itself once the handover
    /// completes, so nothing after this call is guaranteed to run.
    /// </summary>
    public async Task ApplyUpdateAsync()
    {
        if (!_attached)
            return;

        try { await _js.InvokeVoidAsync("ptPwa.applyUpdate"); }
        catch (JSException) { /* worker already gone */ }
        catch (JSDisconnectedException) { /* page tearing down */ }
    }

    private async Task RefreshAsync()
    {
        try
        {
            PwaState state = await _js.InvokeAsync<PwaState>("ptPwa.state");
            CanInstall = state.CanInstall;
            UpdateReady = state.UpdateReady;
            Installed = state.Installed;
            Supported = state.Supported;
            StateChanged?.Invoke();
        }
        catch (JSException) { /* module torn down */ }
        catch (JSDisconnectedException) { /* page tearing down */ }
    }

    /// <summary>Mirror of the object returned by <c>state()</c> in pt-pwa.js.</summary>
    private sealed record PwaState(bool CanInstall, bool UpdateReady, bool Installed, bool Supported);

    public async ValueTask DisposeAsync()
    {
        if (_attached)
        {
            try { await _js.InvokeVoidAsync("ptPwa.detach"); }
            catch (JSDisconnectedException) { /* page already gone */ }
            catch (ObjectDisposedException) { /* page already gone */ }
            catch (JSException) { /* script already gone */ }
        }

        _self?.Dispose();
    }
}
