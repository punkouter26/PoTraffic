namespace PoTraffic.Shared.DTOs.History;

/// <summary>
/// Mean and optional ±1σ travel duration for a 5-minute slot on a given day-of-week.
/// <see cref="StdDevDurationSeconds"/> is null when fewer than 2 records exist for the slot.
/// </summary>
public sealed record BaselineSlotDto(
    string DayOfWeek,
    int TimeSlotBucket,
    double MeanDurationSeconds,
    double? StdDevDurationSeconds,
    int SessionCount);
