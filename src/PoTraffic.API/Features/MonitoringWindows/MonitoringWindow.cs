using System.Text.Json.Serialization;
using PoTraffic.Shared.DTOs.Routes;

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

    /// <summary>
    /// Wire projection. Lives here so the mask→day-names decode has a single definition —
    /// bit order is a storage detail and must not be restated per feature.
    /// </summary>
    public MonitoringWindowDto ToDto() => new(
        Id,
        StartTime.ToString("HH:mm"),
        EndTime.ToString("HH:mm"),
        DecodeDaysOfWeek(DaysOfWeekMask),
        IsActive);

    /// <summary>Expands the <see cref="DaysOfWeekMask"/> bitfield into day names (bit 0 = Monday).</summary>
    public static IReadOnlyList<string> DecodeDaysOfWeek(byte mask)
    {
        string[] names = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
        var days = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            if ((mask & (1 << i)) != 0)
                days.Add(names[i]);
        }
        return days;
    }
}
