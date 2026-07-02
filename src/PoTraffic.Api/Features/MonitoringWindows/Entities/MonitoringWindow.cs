using System.Text.Json.Serialization;

namespace PoTraffic.Api.Features.MonitoringWindows.Entities;

public sealed class MonitoringWindow
{
    public Guid Id { get; set; }
    public Guid RouteId { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }

    /// <summary>Bitfield: bit 0 = Monday … bit 6 = Sunday</summary>
    public byte DaysOfWeekMask { get; set; }

    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    [JsonIgnore]
    public EntityRoute Route { get; set; } = null!;
}
