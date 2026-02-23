namespace PoTraffic.Shared.DTOs.Admin;

public sealed record SystemConfigDto(
    string Key,
    string Value,
    string? Description,
    bool IsSensitive);
