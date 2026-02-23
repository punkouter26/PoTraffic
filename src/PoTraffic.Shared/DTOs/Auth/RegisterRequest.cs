namespace PoTraffic.Shared.DTOs.Auth;

public sealed record RegisterRequest(
    string Email,
    string Password,
    string Locale);
