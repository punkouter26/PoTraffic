using Microsoft.AspNetCore.Components;

namespace PoTraffic.Client.Infrastructure;

/// <summary>
/// Blazor component base that keeps a page's data fresh without burning requests.
/// Override <see cref="LoadDataAsync"/> to fetch and bind state; override
/// <see cref="PollingInterval"/> to set the cadence.
///
/// <para>
/// A plain timer loop is not enough here. The dashboard issues <c>2 + 2N</c> requests
/// per tick, so a ten-route user left a background tab costing twenty-plus requests a
/// minute against a page nobody was looking at. This base:
/// </para>
/// <list type="bullet">
///   <item>stands the timer down while the tab is hidden or the machine is offline,
///         and refreshes immediately when either comes back;</item>
///   <item>backs off exponentially while requests are failing, instead of hammering a
///         dead endpoint at a fixed cadence;</item>
///   <item>drops a tick whose predecessor is still in flight, so a slow refresh cannot
///         pile up behind itself.</item>
/// </list>
///
/// <para>
/// <see cref="LoadDataAsync"/> should let genuine failures propagate: the base treats a
/// thrown exception as the signal to back off. Swallowing it there reports success and
/// defeats the backoff.
/// </para>
/// </summary>
public abstract class PollingComponentBase : ComponentBase, IAsyncDisposable
{
    /// <summary>Failure count past which the backoff stops doubling (2^6 = 64× the interval).</summary>
    private const int MaxBackoffDoublings = 6;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    private int _consecutiveFailures;

    [Inject] private PageActivityMonitor Activity { get; set; } = default!;

    /// <summary>Cadence used while the page is healthy and foregrounded.</summary>
    protected abstract TimeSpan PollingInterval { get; }

    /// <summary>Ceiling the failure backoff climbs to.</summary>
    protected virtual TimeSpan MaxBackoffInterval => TimeSpan.FromMinutes(5);

    /// <summary>
    /// True only for the very first <see cref="LoadDataAsync"/> call. Gate skeleton
    /// placeholders on this — a background refresh must never blank the screen.
    /// </summary>
    protected bool IsFirstLoad { get; private set; } = true;

    /// <summary>True while a refresh is in flight. Drives subtle "syncing" affordances.</summary>
    protected bool IsRefreshing { get; private set; }

    /// <summary>True when the browser reports no connectivity.</summary>
    protected bool IsOffline => !Activity.IsOnline;

    /// <summary>True when the most recent refresh attempt failed, so the view is stale.</summary>
    protected bool IsStale => _consecutiveFailures > 0;

    /// <summary>When data last landed successfully; null until the first success.</summary>
    protected DateTimeOffset? LastSuccessUtc { get; private set; }

    protected override async Task OnInitializedAsync()
    {
        await Activity.EnsureStartedAsync();
        Activity.Resumed += OnResumedAsync;
        Activity.Suspended += OnSuspended;

        await RefreshAsync();
        _loop = RunLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Fetch data and update component state. Called once on init, then on every tick.
    /// Let failures throw — the base converts them into backoff and a stale indicator.
    /// </summary>
    protected abstract Task LoadDataAsync();

    /// <summary>
    /// Refreshes now, unless a refresh is already running. Safe to call from child
    /// component callbacks (Check Now, window saved, route deleted).
    /// </summary>
    protected async Task RefreshAsync()
    {
        // WaitAsync(0) rather than a queue: if a refresh is already in flight, this tick's
        // data is about to arrive anyway. Queuing would serialise redundant round-trips.
        if (!await _refreshGate.WaitAsync(0))
            return;

        IsRefreshing = true;
        try
        {
            await LoadDataAsync();
            LastSuccessUtc = DateTimeOffset.UtcNow;
            _consecutiveFailures = 0;
        }
        catch
        {
            _consecutiveFailures++;
        }
        finally
        {
            IsFirstLoad = false;
            IsRefreshing = false;
            _refreshGate.Release();
        }
    }

    private async Task RefreshAndRenderAsync()
    {
        await RefreshAsync();
        try { await InvokeAsync(StateHasChanged); }
        catch (ObjectDisposedException) { /* component already gone */ }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!IsActive)
                {
                    // Re-read the field each iteration: OnSuspended swaps in a fresh
                    // uncompleted source, and a stale capture would spin.
                    await _resume.Task.WaitAsync(ct);
                    continue;
                }

                await Task.Delay(NextDelay(), ct);

                // The tab may have gone away during the delay.
                if (!IsActive)
                    continue;

                await RefreshAndRenderAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal teardown.
        }
    }

    private bool IsActive => Activity.IsVisible && Activity.IsOnline;

    /// <summary>Healthy cadence, doubled per consecutive failure, capped at <see cref="MaxBackoffInterval"/>.</summary>
    private TimeSpan NextDelay()
    {
        if (_consecutiveFailures == 0)
            return PollingInterval;

        double multiplier = Math.Pow(2, Math.Min(_consecutiveFailures, MaxBackoffDoublings));
        double ms = Math.Min(PollingInterval.TotalMilliseconds * multiplier,
                             MaxBackoffInterval.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms);
    }

    private async Task OnResumedAsync()
    {
        // Coming back from hidden/offline is new information, not another failure —
        // reset the backoff so the user sees current data immediately.
        _consecutiveFailures = 0;
        _resume.TrySetResult();
        await RefreshAndRenderAsync();
    }

    private void OnSuspended() =>
        _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        Activity.Resumed -= OnResumedAsync;
        Activity.Suspended -= OnSuspended;

        await _cts.CancelAsync();
        _resume.TrySetResult(); // release the loop if it is parked on the resume gate

        if (_loop is not null)
        {
            try { await _loop; } catch { /* intentionally swallowed */ }
        }

        _cts.Dispose();
        _refreshGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
