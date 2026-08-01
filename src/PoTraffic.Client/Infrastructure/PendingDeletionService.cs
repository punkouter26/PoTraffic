namespace PoTraffic.Client.Infrastructure;

/// <summary>
/// Holds a delete for a grace period so the user can actually take it back.
///
/// <para>
/// The previous flow issued the DELETE immediately and then showed a toast reading
/// "Undo within 5 seconds…" — with nothing to click and nothing to undo. This inverts
/// it: the route disappears from the list at once (optimistic, so the gesture still
/// feels instant), but the request is only sent when the grace period expires. Undo
/// cancels before anything leaves the browser.
/// </para>
///
/// <para>
/// Hidden and awaiting-commit are tracked separately on purpose. A route stays hidden
/// after its DELETE succeeds, right up until the owning page refreshes — collapsing the
/// two would make the card flash back onto the dashboard for the moment between the
/// request completing and the new route list arriving.
/// </para>
///
/// <para>
/// Scoped per user session and observable, so the dashboard can filter its list and a
/// single undo bar in the layout can render the countdown, without either of them
/// owning the timer.
/// </para>
/// </summary>
public sealed class PendingDeletionService : IDisposable
{
    /// <summary>How long the user has to change their mind.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(6);

    private readonly Dictionary<RouteId, CancellationTokenSource> _awaitingCommit = [];
    private readonly HashSet<RouteId> _hidden = [];

    /// <summary>Raised whenever a deletion is scheduled, undone, committed, or fails.</summary>
    public event Action? Changed;

    /// <summary>The deletion the undo bar is currently offering, if any.</summary>
    public PendingDeletion? Current { get; private set; }

    /// <summary>True while <paramref name="routeId"/> should be kept out of route lists.</summary>
    public bool IsPending(RouteId routeId) => _hidden.Contains(routeId);

    /// <summary>
    /// Hides <paramref name="routeId"/> immediately and runs <paramref name="commit"/> once
    /// the grace period elapses. A route already scheduled is left alone.
    /// </summary>
    /// <param name="label">Human-readable description shown in the undo bar.</param>
    /// <param name="commit">The real delete request. Runs only if the user does not undo.</param>
    /// <param name="onCommitted">Invoked after a successful commit so the owner can refresh.</param>
    /// <param name="onFailed">Invoked when the commit throws; the route is restored first.</param>
    public void Schedule(
        RouteId routeId,
        string label,
        Func<Task> commit,
        Func<Task>? onCommitted = null,
        Func<Task>? onFailed = null)
    {
        if (_awaitingCommit.ContainsKey(routeId))
            return;

        CancellationTokenSource cts = new();
        _awaitingCommit[routeId] = cts;
        _hidden.Add(routeId);
        Current = new PendingDeletion(routeId, label, DateTimeOffset.UtcNow + GracePeriod);
        Changed?.Invoke();

        _ = RunAsync(routeId, commit, onCommitted, onFailed, cts.Token);
    }

    /// <summary>Cancels the pending delete for <paramref name="routeId"/>, restoring it.</summary>
    public void Undo(RouteId routeId)
    {
        if (!_awaitingCommit.Remove(routeId, out CancellationTokenSource? cts))
            return;

        Cancel(cts);
        _hidden.Remove(routeId);
        ClearCurrent(routeId);
        Changed?.Invoke();
    }

    private async Task RunAsync(
        RouteId routeId,
        Func<Task> commit,
        Func<Task>? onCommitted,
        Func<Task>? onFailed,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(GracePeriod, ct);
        }
        catch (OperationCanceledException)
        {
            // Undone — nothing was ever sent.
            return;
        }

        // The window has closed: retire the undo bar, but keep the route hidden.
        _awaitingCommit.Remove(routeId);
        ClearCurrent(routeId);
        Changed?.Invoke();

        try
        {
            await commit();
        }
        catch
        {
            // Put the route back rather than leaving a phantom gap in the list.
            _hidden.Remove(routeId);
            Changed?.Invoke();

            if (onFailed is not null)
            {
                try { await onFailed(); } catch { /* reporting is best-effort */ }
            }
            return;
        }

        if (onCommitted is not null)
        {
            try { await onCommitted(); } catch { /* refresh is best-effort */ }
        }
    }

    private void ClearCurrent(RouteId routeId)
    {
        if (Current?.RouteId == routeId)
            Current = null;
    }

    private static void Cancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
    }

    public void Dispose()
    {
        foreach (CancellationTokenSource cts in _awaitingCommit.Values)
            Cancel(cts);
        _awaitingCommit.Clear();
        _hidden.Clear();
        Current = null;
    }
}

/// <summary>A delete awaiting commit, as rendered by the undo bar.</summary>
/// <param name="RouteId">The hidden route.</param>
/// <param name="Label">Human-readable description, e.g. "Home → Office".</param>
/// <param name="ExpiresAtUtc">When the grace period runs out and the request is sent.</param>
public sealed record PendingDeletion(RouteId RouteId, string Label, DateTimeOffset ExpiresAtUtc);
