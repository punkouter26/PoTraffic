using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Api.Infrastructure.Security;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.Api.Features.Auth;

/// <summary>
/// ANON login for development/testing bypass of OAuth.
/// Each call creates a unique ANON user (e.g. anon463443@potraffic.dev) so that
/// parallel test runs or manual sessions do not collide in the database.
/// All activity is stored under that session's own ANON account.
/// (Standard §6 — ANON bypass with unique suffix per session.)
/// </summary>
public static class AnonAuthExtensions
{
    /// <summary>
    /// Email domain suffix shared by all ANON accounts.
    /// Clients check <c>email.StartsWith("anon") &amp;&amp; email.EndsWith("@potraffic.dev")</c>
    /// to detect ANON sessions and display "ANON LOGGED IN" instead of the raw address.
    /// </summary>
    public const string AnonEmailDomain = "@potraffic.dev";
    public const string AnonEmailPrefix = "anon";

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
        // Each ANON session gets a brand-new unique account so parallel runs never collide.
        // Suffix is 6 random digits (000000–999999), giving 1 million distinct ANON identities.
        int suffix = Random.Shared.Next(100_000, 1_000_000);
        string anonEmail = $"{AnonAuthExtensions.AnonEmailPrefix}{suffix}{AnonAuthExtensions.AnonEmailDomain}";

        // Ensure no accidental collision — retry once if needed
        bool exists = await _db.Set<User>().AnyAsync(u => u.Email == anonEmail, ct);
        if (exists)
        {
            suffix = Random.Shared.Next(100_000, 1_000_000);
            anonEmail = $"{AnonAuthExtensions.AnonEmailPrefix}{suffix}{AnonAuthExtensions.AnonEmailDomain}";
        }

        User anonUser = new()
        {
            Id = Guid.NewGuid(),
            Email = anonEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
            Locale = "en-US",
            Role = "Developer", // Special role for ANON dev/test users
            IsEmailVerified = true,
            EmailVerificationToken = null,
            CreatedAt = DateTimeOffset.UtcNow,
            LastLoginAt = DateTimeOffset.UtcNow
        };

        _db.Set<User>().Add(anonUser);
        await _db.SaveChangesAsync(ct);
        _logger.LogWarning(
            "ANON development user created. Email: {Email}, User ID: {UserId}",
            anonEmail, anonUser.Id);

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
