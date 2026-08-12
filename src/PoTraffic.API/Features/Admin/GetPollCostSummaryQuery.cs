using Microsoft.Extensions.Logging;
using PoTraffic.API.Features.Config;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Admin;

// Per-provider poll cost summary for the current UTC day.
// Post-refactor: pure LINQ over the in-process Table Storage backend.
public sealed record GetPollCostSummaryQuery : IRequest<IReadOnlyList<PollCostSummaryDto>>;

public sealed class GetPollCostSummaryHandler : IRequestHandler<GetPollCostSummaryQuery, IReadOnlyList<PollCostSummaryDto>>
{
    private readonly TableStorageContext _db;
    private readonly ILogger<GetPollCostSummaryHandler> _logger;

    public GetPollCostSummaryHandler(TableStorageContext db, ILogger<GetPollCostSummaryHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<IReadOnlyList<PollCostSummaryDto>> Handle(GetPollCostSummaryQuery query, CancellationToken ct)
    {
        DateTimeOffset dayStart = DateTimeOffset.UtcNow.Date;
        DateTimeOffset dayEnd = dayStart.AddDays(1);

        var polls = _db.Polls
            .Where(p => p.PolledAt >= dayStart && p.PolledAt < dayEnd)
            .ToList();
        var routesById = _db.Routes.ToDictionary(r => r.Id);

        if (polls.Count == 0)
            return Task.FromResult<IReadOnlyList<PollCostSummaryDto>>(Array.Empty<PollCostSummaryDto>());

        PollCostRates rates = PollCostRates.Load(_db);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var summary = polls
            .Where(p => routesById.ContainsKey(p.RouteId))
            .GroupBy(p => (RouteProvider)routesById[p.RouteId].Provider)
            .Select(g =>
            {
                int pollCount = g.Count();
                return new PollCostSummaryDto(
                    AsOfUtc: now,
                    Provider: g.Key,
                    TotalPollCount: pollCount,
                    TotalEstimatedCostUsd: (double)(pollCount * rates.For(g.Key)));
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<PollCostSummaryDto>>(summary);
    }
}
