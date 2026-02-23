using PoTraffic.Shared.Enums;

namespace PoTraffic.Shared.DTOs.Routes;

public sealed record CreateRouteRequest(
    string OriginAddress,
    string DestinationAddress,
    RouteProvider Provider,
    string StartTime = "07:00",
    string EndTime = "09:00",
    byte DaysOfWeekMask = 0x1F);  // 0x1F = Mon–Fri
