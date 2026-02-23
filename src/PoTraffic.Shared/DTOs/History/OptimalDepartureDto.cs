namespace PoTraffic.Shared.DTOs.History;

/// <summary>
/// Suggested optimal departure slot with predicted travel duration and confidence band.
/// </summary>
public sealed record OptimalDepartureDto(
    string DayOfWeek,
    int TimeSlotBucket,
    double PredictedDurationSeconds,
    double? LowerBound,
    double? UpperBound);
