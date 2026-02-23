using Microsoft.AspNetCore.Components;

namespace PoTraffic.Client.Infrastructure;

/// <summary>
/// Blazor component base that drives periodic data refresh via PeriodicTimer.
/// Override <see cref="LoadDataAsync"/> to fetch and bind state.
/// Override <see cref="PollingInterval"/> to set the cadence.
/// Observer pattern — PeriodicTimer drives UI refresh without a SignalR dependency.
/// </summary>
public abstract class PollingComponentBase : ComponentBase, IAsyncDisposable
{
    private PeriodicTimer? _timer;
    private Task? _timerLoop;

    /// <summary>How often <see cref="LoadDataAsync"/> is called after the initial render.</summary>
    protected abstract TimeSpan PollingInterval { get; }

    protected override async Task OnInitializedAsync()
    {
        await LoadDataAsync();
        _timer = new PeriodicTimer(PollingInterval);
        _timerLoop = RunTimerAsync();
    }

    private async Task RunTimerAsync()
    {
        if (_timer is null) return;
        while (await _timer.WaitForNextTickAsync())
        {
            await LoadDataAsync();
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Fetch data and update component state. Called once on init then on every timer tick.</summary>
    protected abstract Task LoadDataAsync();

    public async ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        if (_timerLoop is not null)
        {
            try { await _timerLoop; } catch { /* intentionally swallowed */ }
        }
    }
}
