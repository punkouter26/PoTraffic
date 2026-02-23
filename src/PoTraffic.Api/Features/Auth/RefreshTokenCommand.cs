using MediatR;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Api.Infrastructure.Security;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.Api.Features.Auth;

public sealed record RefreshTokenCommand(
    string RefreshToken) : IRequest<RefreshTokenResult>;

public sealed record RefreshTokenResult(
    bool IsSuccess,
    AuthResponse? Response,
    string? ErrorCode);

public sealed class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, RefreshTokenResult>
{
    private readonly PoTrafficDbContext _db;
    private readonly JwtTokenService _jwt;

    public RefreshTokenCommandHandler(PoTrafficDbContext db, JwtTokenService jwt)
    {
        _db = db;
        _jwt = jwt;
    }

    public async Task<RefreshTokenResult> Handle(RefreshTokenCommand command, CancellationToken ct)
    {
        User? user = await _db.Set<User>()
            .FirstOrDefaultAsync(
                u => u.RefreshToken == command.RefreshToken
                     && u.RefreshTokenExpiry > DateTimeOffset.UtcNow,
                ct);

        if (user is null)
            return new RefreshTokenResult(false, null, "INVALID_REFRESH_TOKEN");

        (string accessToken, string newRefreshToken, DateTimeOffset expiresAt) = _jwt.GenerateTokens(user);

        // Rotate refresh token
        user.RefreshToken = newRefreshToken;
        user.RefreshTokenExpiry = DateTimeOffset.UtcNow.AddDays(_jwt.RefreshTokenExpiryDays);
        await _db.SaveChangesAsync(ct);

        return new RefreshTokenResult(
            true,
            new AuthResponse(accessToken, newRefreshToken, expiresAt, user.Id, user.Role),
            null);
    }
}
