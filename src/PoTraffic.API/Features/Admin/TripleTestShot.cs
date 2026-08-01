using System.Text.Json.Serialization;

namespace PoTraffic.API.Features.Admin;

public sealed class TripleTestShot
{
    public TripleTestShotId Id { get; set; }
    public TripleTestSessionId SessionId { get; set; }

    /// <summary>0, 1, or 2</summary>
    public int ShotIndex { get; set; }

    /// <summary>0, 20, or 40 seconds offset from ScheduledAt</summary>
    public int OffsetSeconds { get; set; }

    public DateTimeOffset? FiredAt { get; set; }
    public bool? IsSuccess { get; set; }
    public int? DurationSeconds { get; set; }
    public int? DistanceMetres { get; set; }
    public string? ErrorCode { get; set; }

    [JsonIgnore]
    public TripleTestSession Session { get; set; } = null!;
}
