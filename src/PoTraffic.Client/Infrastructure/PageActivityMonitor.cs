using Microsoft.JSInterop;

namespace PoTraffic.Client.Infrastructure;

/// <summary>
/// Single subscription to the browser's visibility and connectivity events, shared by
/// every polling component on the page. One JS listener set serves all of them — a
/// per-component subscription would attach the same three handlers N times over.
/// </summary>
public sealed class PageActivityMonitor : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private DotNetObjectReference<PageActivityMonitor>? _selfRef;
    private IJSObjectReference? _module;
    private IJSObjectReference? _cleanup;
    private Task? _initialising;

    public PageActivityMonitor(IJSRuntime js) => _js = js;

    /// <summary>True while the tab is foregrounded. Assumed true until JS says otherwise.</summary>
    public bool IsVisible { get; private set; } = true;

    /// <summary>False while the browser reports the machine offline.</summary>
    public bool IsOnline { get; private set; } = true;

    /// <summary>Raised when the tab becomes visible again, or connectivity returns.</summary>
    public event Func<Task>? Resumed;

    /// <summary>Raised when the tab is backgrounded, so timers can stand down.</summary>
    public event Action? Suspended;

    /// <summary>Idempotent — the first caller wires JS up, the rest await that same task.</summary>
    public Task EnsureStartedAsync() => _initialising ??= StartAsync();

    private async Task StartAsync()
    {
        try
        {
            _module = await _js.InvokeAsync<IJSObjectReference>("import", "./js/pt-activity.js");
            IsVisible = !await _module.InvokeAsync<bool>("isHidden");
            IsOnline = await _module.InvokeAsync<bool>("isOnline");
            _selfRef = DotNetObjectReference.Create(this);
            _cleanup = await _module.InvokeAsync<IJSObjectReference>(
                "watch", _selfRef, nameof(OnActivityChangedAsync));
        }
        catch
        {
            // Prerender or a JS-hostile environment: stay permanently "visible and online",
            // which degrades to the old always-poll behaviour rather than to no polling.
            IsVisible = true;
            IsOnline = true;
        }
    }

    [JSInvokable]
    public async Task OnActivityChangedAsync(string state)
    {
        bool wasActive = IsVisible && IsOnline;

        switch (state)
        {
            case "visible": IsVisible = true; break;
            case "hidden": IsVisible = false; break;
            case "online": IsOnline = true; break;
            case "offline": IsOnline = false; break;
            default: return;
        }

        bool isActive = IsVisible && IsOnline;
        if (isActive == wasActive)
            return;

        if (!isActive)
        {
            Suspended?.Invoke();
            return;
        }

        // Invoking a multicast Func<Task> directly runs every handler but returns only the
        // last one's task, so all but one refresh would be unobserved — and a failure in
        // them silently lost. Walk the invocation list instead.
        if (Resumed is null)
            return;

        foreach (Func<Task> handler in Resumed.GetInvocationList().Cast<Func<Task>>())
        {
            try { await handler(); }
            catch { /* one page's refresh must not stop the others */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cleanup is not null)
        {
            try { await _cleanup.InvokeVoidAsync("call", null); } catch { /* page tearing down */ }
            try { await _cleanup.DisposeAsync(); } catch { /* ignore */ }
        }
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); } catch { /* ignore */ }
        }
        _selfRef?.Dispose();
    }
}
