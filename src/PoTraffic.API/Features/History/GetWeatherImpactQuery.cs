using PoTraffic.API.Features.Routes;
using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.DTOs.History;

namespace PoTraffic.API.Features.History;

public sealed record GetWeatherImpactQuery(RouteId RouteId, UserId UserId)
    : IRequest<WeatherImpactResponse>;

/// <summary>
/// Answers "what does rain actually cost me on this route".
///
/// <para>
/// The obvious implementation — mean duration per condition — is wrong, and confidently so.
/// Weather is not evenly distributed across the clock, so if a route's rainy samples happen
/// to cluster at 08:15 and its clear samples at 06:45, the naive comparison reports the
/// morning peak as the cost of rain. Every sample is therefore scored against the mean of
/// its own 15-minute slot (the same bucketing <see cref="GetBaselineQuery"/> uses), and the
/// conditions are compared on those deltas. What survives is the part of the difference the
/// time of day does not already explain.
/// </para>
///
/// <para>
/// Samples with no recorded condition are counted and reported separately, never folded
/// into Clear — "we did not look" and "it was clear" are different facts.
/// </para>
/// </summary>
public sealed class GetWeatherImpactQueryHandler(TableStorageContext db)
    : IRequestHandler<GetWeatherImpactQuery, WeatherImpactResponse>
{
    public Task<WeatherImpactResponse> Handle(GetWeatherImpactQuery query, CancellationToken ct)
    {
        if (!db.OwnsRoute(query.RouteId, query.UserId))
            return Task.FromResult(Empty(query.RouteId));

        List<PollRecord> polls = [.. db.Polls.Where(p => p.RouteId == query.RouteId)];
        if (polls.Count == 0)
            return Task.FromResult(Empty(query.RouteId));

        // Slot baselines come from every sample, weather-tagged or not: the more history
        // backing a slot's mean, the less a handful of rainy samples can drag the very
        // baseline they are about to be measured against.
        Dictionary<int, double> slotMeans = polls
            .GroupBy(SlotOf)
            .ToDictionary(g => g.Key, g => g.Average(p => (double)p.TravelDurationSeconds));

        List<PollRecord> tagged = [.. polls.Where(p => !string.IsNullOrEmpty(p.WeatherCondition))];

        List<WeatherImpactSlice> slices = [.. tagged
            .GroupBy(p => p.WeatherCondition!)
            .Select(g =>
            {
                double meanDuration = g.Average(p => (double)p.TravelDurationSeconds);
                double meanDelta = g.Average(p => p.TravelDurationSeconds - slotMeans[SlotOf(p)]);

                // The denominator is the mean baseline these samples were measured against,
                // not the overall route mean — otherwise the percentage answers a different
                // question than the delta immediately above it.
                double meanBaseline = g.Average(p => slotMeans[SlotOf(p)]);

                return new WeatherImpactSlice(
                    Condition: g.Key,
                    SampleCount: g.Count(),
                    MeanDurationSeconds: (int)Math.Round(meanDuration),
                    DeltaSeconds: (int)Math.Round(meanDelta),
                    PercentDelta: meanBaseline > 0 ? Math.Round(meanDelta / meanBaseline * 100, 1) : 0,
                    IsConfident: g.Count() >= QuotaConstants.WeatherImpactMinSamples);
            })
            // Fixed condition order rather than "worst first": a card whose rows reshuffle
            // as samples land is unreadable across visits.
            .OrderBy(s => WeatherConditions.Order(s.Condition))];

        return Task.FromResult(new WeatherImpactResponse(
            query.RouteId,
            SampleCount: tagged.Count,
            UnknownCount: polls.Count - tagged.Count,
            MinimumSamples: QuotaConstants.WeatherImpactMinSamples,
            Slices: slices));
    }

    /// <summary>15-minute bucket of the UTC day, matching <see cref="GetBaselineQuery"/>.</summary>
    private static int SlotOf(PollRecord p) => (p.PolledAt.Hour * 4) + (p.PolledAt.Minute / 15);

    private static WeatherImpactResponse Empty(RouteId routeId) =>
        new(routeId, 0, 0, QuotaConstants.WeatherImpactMinSamples, []);
}
