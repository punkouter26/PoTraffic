namespace PoTraffic.Shared.DTOs.Auth;

/// <summary>Server auth state returned by GET /api/auth/me (BFF cookie session).</summary>
public sealed record AuthMeResponse(
    Guid UserId,
    string Email,
    string Role,
    string AuthProvider);
