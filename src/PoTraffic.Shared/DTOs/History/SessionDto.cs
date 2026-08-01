using PoTraffic.Shared.Enums;

namespace PoTraffic.Shared.DTOs.History;

public sealed record SessionDto(
    SessionId Id,
    RouteId RouteId,
    DateOnly SessionDate,
    SessionState State,
    DateTimeOffset? FirstPollAt,
    DateTimeOffset? LastPollAt,
    int PollCount,
    int QuotaConsumed,
    bool IsHolidayExcluded,
    PollScheduleDto? Schedule = null);

/// <summary>
/// When the scheduler will next sample this route, as computed by the scheduler itself.
///
/// <para>
/// The client cannot derive this. Reconstructing it there meant re-parsing "HH:mm" strings
/// and day names back out of the wire format to re-implement <c>PollRouteJob.IsWithinWindow</c>
/// and <c>NextWindowStart</c> — and it still could not see <c>ComputeAdaptiveInterval</c>, so
/// the ETA was wrong by design whenever the server had backed off to the stable cadence.
/// Populated only for a session in <see cref="SessionState.Active"/>.
/// </para>
/// </summary>
public sealed record PollScheduleDto(
    /// <summary>True when the monitoring window contains "now" — both day-of-week and time-of-day.</summary>
    bool WindowIsOpenNow,
    /// <summary>Next instant the window opens; null when no days are enabled.</summary>
    DateTimeOffset? NextWindowOpenUtc,
    /// <summary>When the next sample is due; null when the window is shut or nothing has been sampled yet.</summary>
    DateTimeOffset? NextPollExpectedUtc,
    /// <summary>The cadence currently in force, which adapts to how volatile the route has been.</summary>
    int PollIntervalMinutes);
