using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.Api.Features.Auth;

public sealed record ExternalIdentity(
    string Subject,
    string Email,
    bool IsEmailVerified);

public sealed record ExternalAuthCompletionResult(
    bool IsSuccess,
    string ReturnPath,
    AuthResponse? Response,
    string? ErrorCode);
