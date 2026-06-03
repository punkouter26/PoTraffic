using MediatR;
using PoTraffic.Api.Infrastructure.Storage;

using Microsoft.Extensions.Logging;


using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Admin;

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
            .Where(p => p.PolledAt >= dayStart && p.PolledAt < dayEnd && !p.IsDeleted)
            .ToList();
        var routesById = _db.Routes.ToDictionary(r => r.Id);

        if (polls.Count == 0)
            return Task.FromResult<IReadOnlyList<PollCostSummaryDto>>(Array.Empty<PollCostSummaryDto>());

        var configs = _db.Configurations
            .Where(c => c.Key.StartsWith("cost.perpoll."))
            .ToList();

        double googleCost = GetCost(configs, "cost.perpoll.googlemaps");
        double tomtomCost = GetCost(configs, "cost.perpoll.tomtom");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var summary = polls
            .Where(p => routesById.ContainsKey(p.RouteId))
            .GroupBy(p => (RouteProvider)routesById[p.RouteId].Provider)
            .Select(g =>
            {
                RouteProvider provider = g.Key;
                double costPerPoll = provider == RouteProvider.TomTom ? tomtomCost : googleCost;
                return new PollCostSummaryDto(
                    AsOfUtc: now,
                    Provider: provider,
                    TotalPollCount: g.Count(),
                    TotalEstimatedCostUsd: g.Count() * costPerPoll);
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<PollCostSummaryDto>>(summary);
    }

    private static double GetCost(List<PoTraffic.Api.Features.Config.Entities.SystemConfiguration> configs, string key)
    {
        string? value = configs.FirstOrDefault(c => c.Key == key)?.Value;
        return double.TryParse(value, out double result) ? result : 0.0;
    }
}
