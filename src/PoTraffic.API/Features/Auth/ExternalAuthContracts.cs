namespace PoTraffic.API.Features.Auth;

public sealed record ExternalIdentity(
    string Subject,
    string Email,
    bool IsEmailVerified);

public sealed record ExternalAuthCompletionResult(
    bool IsSuccess,
    string ReturnPath,
    User? User,
    string? ErrorCode);
