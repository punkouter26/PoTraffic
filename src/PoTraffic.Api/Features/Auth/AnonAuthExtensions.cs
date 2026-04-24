using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Api.Infrastructure.Security;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.Api.Features.Auth;

/// <summary>
/// ANON login for development/testing bypass of OAuth.
/// Creates or uses a system ANON user for testing without credentials.
/// All actions are logged under the ANON account.
/// </summary>
public static class AnonAuthExtensions
{
    /// <summary>
    /// GUID reserved for the ANON development user.
    /// </summary>
    public static readonly Guid AnonUserId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// Email for the ANON development user.
    /// </summary>
    public const string AnonEmail = "anon@potraffic.dev";

    public static IEndpointRouteBuilder MapAnonEndpoints(this IEndpointRouteBuilder app)
    {
        // Development-only ANON login bypass
        app.MapPost("/api/auth/anon-login", async (
            ISender sender,
            IWebHostEnvironment env) =>
        {
            // Only allow ANON login in Development or Testing environments
            if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
            {
                return Results.Forbid();
            }

            // Forward to the actual ANON login handler
            AnonLoginResult result = await sender.Send(new AnonLoginCommand());
            return result.IsSuccess
                ? Results.Ok(result.Response)
                : Results.BadRequest(new { error = result.ErrorCode });
        })
        .WithTags("Auth")
        .WithName("AnonLogin")
        .ExcludeFromDescription() // Hide from OpenAPI
        .AllowAnonymous();

        return app;
    }
}

public sealed record AnonLoginCommand : IRequest<AnonLoginResult>;

public sealed record AnonLoginResult(
    bool IsSuccess,
    AuthResponse? Response,
    string? ErrorCode);

public sealed class AnonLoginCommandHandler : IRequestHandler<AnonLoginCommand, AnonLoginResult>
{
    private readonly PoTrafficDbContext _db;
    private readonly JwtTokenService _jwt;
    private readonly ILogger<AnonLoginCommandHandler> _logger;

    public AnonLoginCommandHandler(
        PoTrafficDbContext db,
        JwtTokenService jwt,
        ILogger<AnonLoginCommandHandler> logger)
    {
        _db = db;
        _jwt = jwt;
        _logger = logger;
    }

    public async Task<AnonLoginResult> Handle(AnonLoginCommand command, CancellationToken ct)
    {
        // Find or create ANON user
        User? anonUser = await _db.Set<User>()
            .FirstOrDefaultAsync(u => u.Email == AnonAuthExtensions.AnonEmail, ct);

        if (anonUser is null)
        {
            anonUser = new User
            {
                Id = AnonAuthExtensions.AnonUserId,
                Email = AnonAuthExtensions.AnonEmail,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()), // Random hash
                Locale = "en-US",
                Role = "Developer", // Special role for ANON users
                IsEmailVerified = true,
                EmailVerificationToken = null,
                CreatedAt = DateTimeOffset.UtcNow,
                LastLoginAt = DateTimeOffset.UtcNow
            };
            _db.Set<User>().Add(anonUser);
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning("ANON development user created. User ID: {UserId}", anonUser.Id);
        }
        else
        {
            // Update last login
            anonUser.LastLoginAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogWarning("ANON login bypass used. User ID: {UserId}", anonUser.Id);

        // Generate tokens
        (string accessToken, string refreshToken, DateTimeOffset expiresAt) = _jwt.GenerateTokens(anonUser);

        anonUser.RefreshToken = refreshToken;
        anonUser.RefreshTokenExpiry = DateTimeOffset.UtcNow.AddDays(_jwt.RefreshTokenExpiryDays);
        await _db.SaveChangesAsync(ct);

        return new AnonLoginResult(
            true,
            new AuthResponse(accessToken, refreshToken, expiresAt, anonUser.Id, anonUser.Role),
            null);
    }
}
