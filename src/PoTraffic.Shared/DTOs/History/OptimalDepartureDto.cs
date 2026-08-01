namespace PoTraffic.Shared.DTOs.History;

/// <summary>
/// Suggested optimal departure slot with predicted travel duration and confidence band.
/// </summary>
/// <param name="SampleCount">
/// How many poll records the recommendation rests on. Surfaced so the client can show how
/// far along the baseline is instead of a bare "still building…" with no end in sight — a
/// recommendation from four samples and one from four hundred should not look identical.
/// </param>
/// <param name="DayOfWeekSpecific">
/// False when the requested weekday was too sparse and all days were used instead, matching
/// <see cref="BaselineResponse.DayOfWeekSpecific"/>.
/// </param>
public sealed record OptimalDepartureDto(
    string DayOfWeek,
    int TimeSlotBucket,
    double PredictedDurationSeconds,
    double? LowerBound,
    double? UpperBound,
    int SampleCount = 0,
    bool DayOfWeekSpecific = true);
