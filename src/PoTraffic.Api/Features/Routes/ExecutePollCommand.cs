using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Routes;

public sealed record ExecutePollCommand(Guid RouteId) : IRequest<bool>;

public sealed class ExecutePollCommandHandler(
    PoTrafficDbContext db,
    ITrafficProviderFactory providerFactory,
    ILogger<ExecutePollCommandHandler> logger) : IRequestHandler<ExecutePollCommand, bool>
{
    public async Task<bool> Handle(ExecutePollCommand cmd, CancellationToken ct)
    {
        // 1. Load Route + active MonitoringSession for today
        EntityRoute? route = await db.Routes
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == cmd.RouteId
                && r.MonitoringStatus != (int)MonitoringStatus.Deleted, ct);

        if (route is null)
        {
            logger.LogWarning("ExecutePollCommand: Route {RouteId} not found or deleted", cmd.RouteId);
            return false;
        }

        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.Date);

        MonitoringSession? session = await db.MonitoringSessions
            .FirstOrDefaultAsync(s => s.RouteId == cmd.RouteId
                && s.SessionDate == today
                && s.State == (int)SessionState.Active, ct);

        // 2. If no active session, log and return false
        if (session is null)
        {
            logger.LogInformation("ExecutePollCommand: No active session for route {RouteId} on {Date}",
                cmd.RouteId, today);
            return false;
        }

        // 3. Resolve provider via factory (resolves keyed DI lookup)
        ITrafficProvider provider = providerFactory.GetProvider((RouteProvider)route.Provider);

        TravelResult? travelResult;

        try
        {
            // 4. Call provider — catch all exceptions; Hangfire must not retry on provider errors
            travelResult = await provider.GetTravelTimeAsync(
                route.OriginCoordinates,
                route.DestinationCoordinates,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "ExecutePollCommand: Provider error for route {RouteId} — poll skipped", cmd.RouteId);
            return false;
        }

        if (travelResult is null)
        {
            logger.LogWarning("ExecutePollCommand: Provider returned null for route {RouteId}", cmd.RouteId);
            return false;
        }

        // 5. Create PollRecord
        var record = new PollRecord
        {
            RouteId = cmd.RouteId,
            SessionId = session.Id,
            PolledAt = DateTimeOffset.UtcNow,
            TravelDurationSeconds = travelResult.DurationSeconds,
            DistanceMetres = travelResult.DistanceMetres,
            RawProviderResponse = travelResult.RawJson
        };

        // 6. Reroute detection
        List<PollRecord> priorRecords = await db.PollRecords
            .Where(p => p.SessionId == session.Id && !p.IsDeleted)
            .OrderByDescending(p => p.PolledAt)
            .ToListAsync(ct);

        if (priorRecords.Count >= 2)
        {
            // Calculate session median distance from all prior records
            double medianDistance = CalculateMedian(priorRecords.Select(p => (double)p.DistanceMetres).ToList());
            double threshold = medianDistance * (1.0 + QuotaConstants.RerouteDistanceThresholdPercent / 100.0);

            bool currentElevated = record.DistanceMetres >= threshold;
            bool priorElevated = priorRecords[0].DistanceMetres >= threshold;

            if (currentElevated && priorElevated)
            {
                record.IsRerouted = true;
                logger.LogInformation(
                    "Reroute detected for route {RouteId}: current={Current}m, prior={Prior}m, median={Median}m",
                    cmd.RouteId, record.DistanceMetres, priorRecords[0].DistanceMetres, medianDistance);
            }
        }

        db.PollRecords.Add(record);

        // 7. Update session statistics
        session.LastPollAt = record.PolledAt;
        session.PollCount += 1;
        session.FirstPollAt ??= record.PolledAt;

        // 8. Save
        await db.SaveChangesAsync(ct);

        return true;
    }

    /// <summary>Computes the median of a list of doubles. Exposed public for test reuse.</summary>
    public static double CalculateMedian(List<double> values)
    {
        if (values.Count == 0) return 0;
        List<double> sorted = [.. values.OrderBy(v => v)];
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }
}
