namespace PoTraffic.Shared.DTOs.Account;

public sealed record ProfileDto(
    Guid UserId,
    string Email,
    string Locale,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    string Role);
