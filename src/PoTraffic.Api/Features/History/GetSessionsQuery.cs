using MediatR;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Shared.DTOs.History;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.History;

public sealed record GetSessionsQuery(
    Guid RouteId,
    Guid UserId) : IRequest<IReadOnlyList<SessionDto>>;

public sealed class GetSessionsQueryHandler
    : IRequestHandler<GetSessionsQuery, IReadOnlyList<SessionDto>>
{
    private readonly PoTrafficDbContext _db;

    public GetSessionsQueryHandler(PoTrafficDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SessionDto>> Handle(
        GetSessionsQuery query,
        CancellationToken ct)
    {
        return await _db.MonitoringSessions
            .Where(s => s.RouteId == query.RouteId && s.Route.UserId == query.UserId)
            .OrderByDescending(s => s.SessionDate)
            .Select(s => new SessionDto(
                s.Id,
                s.RouteId,
                s.SessionDate,
                (SessionState)s.State,
                s.FirstPollAt,
                s.LastPollAt,
                s.PollCount,
                s.QuotaConsumed,
                s.IsHolidayExcluded))
            .ToListAsync(ct);
    }
}
