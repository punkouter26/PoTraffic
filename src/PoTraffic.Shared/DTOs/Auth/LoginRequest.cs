namespace PoTraffic.Shared.DTOs.Auth;

public sealed record LoginRequest(
    string Email,
    string Password);
