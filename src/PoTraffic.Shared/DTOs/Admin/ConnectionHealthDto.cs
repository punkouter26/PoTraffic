namespace PoTraffic.Shared.DTOs.Admin;

/// <summary>Live status of a single external dependency as returned by the health check subsystem.</summary>
public sealed record ConnectionHealthDto(
    string Name,
    string Status,
    string? Description,
    double DurationMs);
