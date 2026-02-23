using PoTraffic.Shared.Enums;

namespace PoTraffic.Shared.DTOs.History;

public sealed record PollRecordDto(
    Guid Id,
    Guid? SessionId,
    DateTimeOffset PolledAt,
    int TravelDurationSeconds,
    int DistanceMetres,
    RouteProvider Provider,
    bool IsRerouted)
{
    // Radzen chart CategoryProperty requires DateTime, not DateTimeOffset
    public DateTime PolledAtDateTime => PolledAt.LocalDateTime;
}
