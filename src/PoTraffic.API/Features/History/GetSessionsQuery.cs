using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.History;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.History;

public sealed record GetSessionsQuery(
    RouteId RouteId,
    UserId UserId) : IRequest<IReadOnlyList<SessionDto>>;

public sealed class GetSessionsQueryHandler
    : IRequestHandler<GetSessionsQuery, IReadOnlyList<SessionDto>>
{
    private readonly TableStorageContext _db;

    public GetSessionsQueryHandler(TableStorageContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SessionDto>> Handle(
        GetSessionsQuery query,
        CancellationToken ct)
    {
        // Ownership check — TableStorageContext has no navigation properties, so the
        // old `s.Route.UserId` access NREs. Resolve ownership explicitly instead.
        if (!_db.OwnsRoute(query.RouteId, query.UserId))
            return Array.Empty<SessionDto>();

        // The scheduler's own view of when this route is next sampled, resolved here so the
        // client renders a timestamp instead of re-deriving window and cadence rules it
        // cannot see (see PollScheduleDto). Both lookups are hoisted out of the projection —
        // they are the same for every session of this route.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MonitoringWindow? activeWindow = _db.MonitoringWindows
            .FirstOrDefault(w => w.RouteId == query.RouteId && w.IsActive);
        TimeSpan interval = PollRouteJob.ComputeAdaptiveInterval(_db, query.RouteId, now);

        return [.. _db.MonitoringSessions
            .Where(s => s.RouteId == query.RouteId)
            .OrderByDescending(s => s.SessionDate)
            .AsEnumerable()
            .Select(s => new SessionDto(
                s.Id,
                s.RouteId,
                s.SessionDate,
                (SessionState)s.State,
                s.FirstPollAt,
                s.LastPollAt,
                s.PollCount,
                s.QuotaConsumed,
                s.IsHolidayExcluded,
                BuildSchedule(s)))];

        PollScheduleDto? BuildSchedule(MonitoringSession session)
        {
            if ((SessionState)session.State != SessionState.Active || activeWindow is null)
                return null;

            bool open = PollRouteJob.IsWithinWindow(activeWindow, now);

            return new PollScheduleDto(
                WindowIsOpenNow: open,
                NextWindowOpenUtc: PollRouteJob.NextWindowStart(activeWindow, now),
                // Only meaningful while the window is open: outside it the chain sleeps until
                // the window reopens, which NextWindowOpenUtc already reports.
                NextPollExpectedUtc: open && session.LastPollAt is { } last ? last.Add(interval) : null,
                PollIntervalMinutes: (int)interval.TotalMinutes);
        }
    }
}
