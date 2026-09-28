namespace PoTraffic.Shared.DTOs.History;

/// <summary>
/// "Leave by <see cref="LeaveBy"/> to arrive by <see cref="ArriveBy"/>": the latest 5-minute
/// departure slot whose 90th-percentile trip still lands on time.
/// </summary>
/// <param name="ArriveBy">The route's target arrival, local "HH:mm".</param>
/// <param name="LeaveBy">Local "HH:mm", or null when no slot before the target has enough history yet.</param>
/// <param name="TypicalSeconds">Median trip leaving at <see cref="LeaveBy"/>.</param>
/// <param name="WorstCaseSeconds">90th-percentile trip leaving at <see cref="LeaveBy"/> — the one the plan is built on.</param>
/// <param name="OnTimePercent">Share of recorded trips from that slot that arrived by <see cref="ArriveBy"/>.</param>
/// <param name="SampleCount">Trips the recommendation rests on.</param>
/// <param name="DayOfWeekSpecific">False when the weekday was too sparse and every day was used.</param>
public sealed record DeparturePlanDto(
    string ArriveBy,
    string? LeaveBy,
    int? TypicalSeconds,
    int? WorstCaseSeconds,
    int OnTimePercent,
    int SampleCount,
    bool DayOfWeekSpecific);
