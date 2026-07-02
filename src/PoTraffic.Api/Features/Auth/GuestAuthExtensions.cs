using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.Api.Features.Auth;

/// <summary>
/// GUEST login for integration/E2E test bypass of OAuth.
/// Each call creates a unique GUEST user (e.g. guest46344343@potraffic.dev) so
/// that parallel test runs or manual sessions do not collide in the database.
/// The session is issued as the standard BFF cookie.
///
/// (Rule 6 — GUEST Mode: hidden outside Testing; "GUEST12345678 LOGGED IN" in the nav bar.)
/// (Rule 13 — Microsoft OAuth is required outside Testing.)
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
        // Testing-only GUEST login bypass for integration tests.
        app.MapPost("/api/auth/guest-login", async (
            HttpContext httpContext,
            ISender sender,
            IWebHostEnvironment env) =>
        {
            // Only allow GUEST login in Testing. In Development and Production,
            // this endpoint is not registered (see Program.cs).
            if (!env.IsEnvironment("Testing"))
            {
                return Results.NotFound();
            }

            GuestLoginResult result = await sender.Send(new GuestLoginCommand());
            if (!result.IsSuccess || result.User is null)
                return Results.BadRequest(new { error = result.ErrorCode });

            User user = result.User;
            await CookieSignIn.SignInAsync(httpContext, user);
            return Results.Ok(new AuthMeResponse(user.Id, user.Email, user.Role, user.AuthProvider));
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
    User? User,
    string? ErrorCode);

public sealed class GuestLoginCommandHandler(
    TableStorageContext db,
    ILogger<GuestLoginCommandHandler> logger) : IRequestHandler<GuestLoginCommand, GuestLoginResult>
{
    public async Task<GuestLoginResult> Handle(GuestLoginCommand command, CancellationToken ct)
    {
        // Each GUEST session gets a brand-new unique account so parallel runs never collide.
        // Suffix is 8 random digits (00000000–99999999), giving 100M distinct GUEST identities.
        int min = (int)Math.Pow(10, GuestAuthExtensions.GuestSuffixLength - 1);
        int max = (int)Math.Pow(10, GuestAuthExtensions.GuestSuffixLength);
        int suffix = Random.Shared.Next(min, max);
        string guestEmail = $"{GuestAuthExtensions.GuestEmailPrefix}{suffix}{GuestAuthExtensions.GuestEmailDomain}";

        // Ensure no accidental collision — retry once if needed
        bool exists = db.Users.Any(u => u.Email == guestEmail);
        if (exists)
        {
            suffix = Random.Shared.Next(min, max);
            guestEmail = $"{GuestAuthExtensions.GuestEmailPrefix}{suffix}{GuestAuthExtensions.GuestEmailDomain}";
        }

        User guestUser = new()
        {
            Id = Guid.NewGuid(),
            Email = guestEmail,
            Locale = "en-US",
            Role = "Guest", // Special role for GUEST test users
            AuthProvider = "guest",
            IsEmailVerified = true,
            CreatedAt = DateTimeOffset.UtcNow,
            LastLoginAt = DateTimeOffset.UtcNow
        };

        db.Add(guestUser);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("GUEST session created: {Email}", guestEmail);

        return new GuestLoginResult(true, guestUser, null);
    }
}
