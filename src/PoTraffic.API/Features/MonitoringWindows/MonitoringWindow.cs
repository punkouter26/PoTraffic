using System.Text.Json.Serialization;

namespace PoTraffic.API.Features.MonitoringWindows;

public sealed class MonitoringWindow
{
    public WindowId Id { get; set; }
    public RouteId RouteId { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }

    /// <summary>Bitfield: bit 0 = Monday … bit 6 = Sunday</summary>
    public byte DaysOfWeekMask { get; set; }

    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    [JsonIgnore]
    public EntityRoute Route { get; set; } = null!;
}
