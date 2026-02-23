using MediatR;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Admin;

public sealed record GetTripleTestSessionQuery(Guid SessionId) : IRequest<TripleTestSessionDto?>;

public sealed class GetTripleTestSessionQueryHandler : IRequestHandler<GetTripleTestSessionQuery, TripleTestSessionDto?>
{
    private readonly PoTrafficDbContext _db;

    public GetTripleTestSessionQueryHandler(PoTrafficDbContext db) => _db = db;

    public async Task<TripleTestSessionDto?> Handle(GetTripleTestSessionQuery query, CancellationToken ct)
    {
        TripleTestSession? session = await _db.TripleTestSessions
            .Include(s => s.Shots.OrderBy(sh => sh.ShotIndex))
            .FirstOrDefaultAsync(s => s.Id == query.SessionId, ct);

        if (session is null)
            return null;

        IReadOnlyList<TripleTestShotDto> shots = session.Shots
            .OrderBy(sh => sh.ShotIndex)
            .Select(sh => new TripleTestShotDto(
                sh.ShotIndex,
                sh.OffsetSeconds,
                sh.FiredAt,
                sh.IsSuccess,
                sh.DurationSeconds,
                sh.DistanceMetres,
                sh.ErrorCode))
            .ToList();

        // Compute winner and averages from completed successful shots only
        IReadOnlyList<TripleTestShotDto> successful = shots
            .Where(s => s.IsSuccess == true && s.DurationSeconds.HasValue)
            .ToList();

        int? idealShotIndex = successful.Count > 0
            ? successful.MinBy(s => s.DurationSeconds!.Value)!.ShotIndex
            : null;

        double? avgDuration = successful.Count > 0
            ? successful.Average(s => (double)s.DurationSeconds!.Value)
            : null;

        double? avgDistance = successful.Count > 0
            ? successful.Average(s => (double)s.DistanceMetres!.Value)
            : null;

        return new TripleTestSessionDto(
            session.Id,
            session.OriginAddress,
            session.DestinationAddress,
            (RouteProvider)session.Provider,
            session.ScheduledAt,
            shots,
            idealShotIndex,
            avgDuration,
            avgDistance);
    }
}
