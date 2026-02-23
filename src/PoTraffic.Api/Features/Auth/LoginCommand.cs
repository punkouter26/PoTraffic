using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Api.Infrastructure.Security;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.Api.Features.Auth;

public sealed record LoginCommand(
    string Email,
    string Password) : IRequest<LoginResult>;

public sealed record LoginResult(
    bool IsSuccess,
    AuthResponse? Response,
    string? ErrorCode);

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public sealed class LoginCommandHandler : IRequestHandler<LoginCommand, LoginResult>
{
    private readonly PoTrafficDbContext _db;
    private readonly JwtTokenService _jwt;
    private readonly ILogger<LoginCommandHandler> _logger;

    public LoginCommandHandler(
        PoTrafficDbContext db,
        JwtTokenService jwt,
        ILogger<LoginCommandHandler> logger)
    {
        _db = db;
        _jwt = jwt;
        _logger = logger;
    }

    public async Task<LoginResult> Handle(LoginCommand command, CancellationToken ct)
    {
        User? user = await _db.Set<User>()
            .FirstOrDefaultAsync(u => u.Email == command.Email, ct);

        if (user is null || !BCrypt.Net.BCrypt.Verify(command.Password, user.PasswordHash))
            return new LoginResult(false, null, "INVALID_CREDENTIALS");

        (string accessToken, string refreshToken, DateTimeOffset expiresAt) = _jwt.GenerateTokens(user);

        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiry = DateTimeOffset.UtcNow.AddDays(_jwt.RefreshTokenExpiryDays);
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("User {UserId} logged in", user.Id);

        return new LoginResult(
            true,
            new AuthResponse(accessToken, refreshToken, expiresAt, user.Id, user.Role),
            null);
    }
}
