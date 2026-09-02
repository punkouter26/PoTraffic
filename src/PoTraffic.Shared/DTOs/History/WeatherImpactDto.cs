namespace PoTraffic.Shared.DTOs.History;

/// <summary>
/// What each weather condition costs this route, measured against the route's own
/// time-of-day baseline rather than against its overall mean — see
/// <c>GetWeatherImpactQuery</c> for why the naive comparison is misleading.
/// </summary>
public sealed record WeatherImpactResponse(
    RouteId RouteId,
    int SampleCount,
    int UnknownCount,
    int MinimumSamples,
    IReadOnlyList<WeatherImpactSlice> Slices);

/// <summary>One condition's effect on this route.</summary>
/// <param name="Condition">One of the <c>WeatherConditions</c> buckets.</param>
/// <param name="SampleCount">Polls recorded under this condition.</param>
/// <param name="MeanDurationSeconds">Mean travel time under this condition.</param>
/// <param name="DeltaSeconds">
/// Mean signed difference against the same time-of-day slot's baseline. Positive is slower.
/// </param>
/// <param name="PercentDelta">The same difference as a percentage of the slot baseline.</param>
/// <param name="IsConfident">
/// False when this condition has fewer than <c>MinimumSamples</c> polls. The slice is still
/// returned — "we have seen 3 snowy polls" is worth showing — but must not be presented as
/// a settled number.
/// </param>
public sealed record WeatherImpactSlice(
    string Condition,
    int SampleCount,
    int MeanDurationSeconds,
    int DeltaSeconds,
    double PercentDelta,
    bool IsConfident);
