using MediatR;
using PoTraffic.Api.Infrastructure.Storage;

using Microsoft.Extensions.Logging;


using PoTraffic.Api.Infrastructure.Security;

using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.Api.Features.Auth;

/// <summary>
/// GUEST login for development/testing bypass of OAuth.
/// Each call creates a unique GUEST user (e.g. guest46344343@potraffic.dev) so
/// that parallel test runs or manual sessions do not collide in the database.
/// All activity is stored under that session's own GUEST account.
///
/// (Rule 6 — GUEST Mode: Persistence via LocalStorage; hidden in Prod;
///  "GUEST12345678 LOGGED IN" shown in the nav bar.)
/// (Rule 13 — Microsoft OAuth + Guest is allowed in dev/test env.)
/// </summary>
public static class GuestAuthExtensions
{
    /// <summary>
    /// Email domain suffix shared by all GUEST accounts.
    /// Clients check <c>email.StartsWith("guest") &amp;&amp; email.EndsWith("@potraffic.dev")</c>
    /// to detect GUEST sessions and display "GUEST{n} LOGGED IN" in the nav bar.
    /// </summary>
    public const string GuestEmailDomain = "@potraffic.dev";
    public const string GuestEmailPrefix = "guest";

    /// <summary>
    /// Number of random digits appended to the GUEST prefix.
    /// 8 digits = 100,000,000 unique GUEST identities (Rule 13).
    /// </summary>
    public const int GuestSuffixLength = 8;

    public static IEndpointRouteBuilder MapGuestEndpoints(this IEndpointRouteBuilder app)
    {
        // Development-only GUEST login bypass (Rule 6: hidden + disabled in Prod).
        app.MapPost("/api/auth/guest-login", async (
            ISender sender,
            IWebHostEnvironment env) =>
        {
            // Only allow GUEST login in Development or Testing environments.
            // In Production this endpoint is NOT registered (see Program.cs).
            if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
            {
                return Results.NotFound();
            }

            GuestLoginResult result = await sender.Send(new GuestLoginCommand());
            return result.IsSuccess
                ? Results.Ok(result.Response)
                : Results.BadRequest(new { error = result.ErrorCode });
        })
        .WithTags("Auth")
        .WithName("GuestLogin")
        .ExcludeFromDescription() // Hide from OpenAPI
        .AllowAnonymous();

        return app;
    }
}

public sealed record GuestLoginCommand : IRequest<GuestLoginResult>;

public sealed record GuestLoginResult(
    bool IsSuccess,
    AuthResponse? Response,
    string? ErrorCode);

public sealed class GuestLoginCommandHandler : IRequestHandler<GuestLoginCommand, GuestLoginResult>
{
    private readonly TableStorageContext _db;
    private readonly JwtTokenService _jwt;
    private readonly ILogger<GuestLoginCommandHandler> _logger;

    public GuestLoginCommandHandler(
        TableStorageContext db,
        JwtTokenService jwt,
        ILogger<GuestLoginCommandHandler> logger)
    {
        _db = db;
        _jwt = jwt;
        _logger = logger;
    }

    public async Task<GuestLoginResult> Handle(GuestLoginCommand command, CancellationToken ct)
    {
        // Each GUEST session gets a brand-new unique account so parallel runs never collide.
        // Suffix is 8 random digits (00000000–99999999), giving 100M distinct GUEST identities.
        int min = (int)Math.Pow(10, GuestAuthExtensions.GuestSuffixLength - 1);
        int max = (int)Math.Pow(10, GuestAuthExtensions.GuestSuffixLength);
        int suffix = Random.Shared.Next(min, max);
        string guestEmail = $"{GuestAuthExtensions.GuestEmailPrefix}{suffix}{GuestAuthExtensions.GuestEmailDomain}";

        // Ensure no accidental collision — retry once if needed
        bool exists = _db.Users.Any(u => u.Email == guestEmail);
        if (exists)
        {
            suffix = Random.Shared.Next(min, max);
            guestEmail = $"{GuestAuthExtensions.GuestEmailPrefix}{suffix}{GuestAuthExtensions.GuestEmailDomain}";
        }

        User guestUser = new()
        {
            Id = Guid.NewGuid(),
            Email = guestEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
            Locale = "en-US",
            Role = "Guest", // Special role for GUEST dev/test users
            AuthProvider = "guest",
            IsEmailVerified = true,
            EmailVerificationToken = null,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Add(guestUser);
        await _db.SaveChangesAsync(ct);

        (string accessToken, string refreshToken, DateTimeOffset expiresAt) = _jwt.GenerateTokens(guestUser);

        guestUser.RefreshToken = refreshToken;
        guestUser.RefreshTokenExpiry = DateTimeOffset.UtcNow.AddDays(_jwt.RefreshTokenExpiryDays);
        guestUser.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("GUEST session created: {Email}", guestEmail);

        return new GuestLoginResult(
            true,
            new AuthResponse(accessToken, refreshToken, expiresAt, guestUser.Id, guestUser.Role),
            null);
    }
}
