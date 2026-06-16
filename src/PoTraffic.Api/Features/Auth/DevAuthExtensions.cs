using MediatR;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Security;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.Api.Features.Auth;

/// <summary>
/// Development-only conveniences that make the FULL app verifiable offline —
/// without Microsoft OAuth (which needs a human) or a Google Maps API key
/// (which the dev environment doesn't have).
///
/// Provides:
///   • A deterministic seeded Administrator account (<see cref="DevAdminEmail"/>).
///   • A pre-geocoded sample route owned by that admin, with ~2 weeks of synthetic
///     morning-peak poll history so route-detail charts and the history view render.
///   • POST /api/auth/dev-admin-login — logs in AS that admin so admin grids
///     (/admin, /diag) and the seeded route data are reachable.
///
/// All of this is registered ONLY in the Development environment (Program.cs guards it).
/// </summary>
public static class DevAuthExtensions
{
    /// <summary>Fixed id so the seeded route's owner always matches the dev-admin login.</summary>
    public static readonly Guid DevAdminId = new("d0d0d0d0-0000-4000-8000-000000000001");
    public const string DevAdminEmail = "dev-admin@potraffic.dev";

    public static IEndpointRouteBuilder MapDevAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/dev-admin-login", async (
            ISender sender,
            IWebHostEnvironment env) =>
        {
            // Never available outside Development (Program.cs also guards the call site).
            if (!env.IsDevelopment())
                return Results.NotFound();

            DevAdminLoginResult result = await sender.Send(new DevAdminLoginCommand());
            return result.IsSuccess
                ? Results.Ok(result.Response)
                : Results.BadRequest(new { error = result.ErrorCode });
        })
        .WithTags("Auth")
        .WithName("DevAdminLogin")
        .ExcludeFromDescription()
        .AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Ensures the deterministic dev Administrator user exists. Idempotent.
    /// </summary>
    public static User EnsureDevAdminUser(TableStorageContext db)
    {
        User? admin = db.Users.FirstOrDefault(u => u.Id == DevAdminId || u.Email == DevAdminEmail);
        if (admin is not null)
            return admin;

        admin = new User
        {
            Id = DevAdminId,
            Email = DevAdminEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
            Locale = "en-US",
            Role = "Administrator",
            AuthProvider = "dev",
            IsEmailVerified = true,
            EmailVerificationToken = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Add(admin);
        return admin;
    }

    /// <summary>
    /// Seeds the dev admin + one pre-geocoded sample route with ~2 weeks of
    /// synthetic morning-peak poll history. Idempotent — safe on every cold start.
    /// </summary>
    public static async Task SeedDevDataIfMissingAsync(TableStorageContext db, ILogger logger)
    {
        User admin = EnsureDevAdminUser(db);

        if (db.Routes.Any(r => r.UserId == admin.Id))
        {
            await db.SaveChangesAsync();
            return;
        }

        var route = new EntityRoute
        {
            Id = new Guid("d0d0d0d0-0000-4000-8000-000000000002"),
            UserId = admin.Id,
            OriginAddress = "Seattle, WA",
            OriginCoordinates = "47.6062,-122.3321",
            DestinationAddress = "Redmond, WA",
            DestinationCoordinates = "47.6740,-122.1215",
            Provider = 0,
            MonitoringStatus = (int)PoTraffic.Shared.Enums.MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Add(route);

        // ~2 weeks of weekday morning-peak (07:00–09:00, 5-min cadence) poll history
        // with realistic volatility and the occasional reroute anomaly.
        const int baselineSeconds = 1500; // ~25 min
        int polls = 0;
        for (int d = 0; d < 14; d++)
        {
            DateTimeOffset date = DateTimeOffset.UtcNow.Date.AddDays(-d);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;

            for (int h = 7; h <= 9; h++)
            {
                for (int m = 0; m < 60; m += 5)
                {
                    bool isAnomalous = Random.Shared.Next(1, 100) > 92;
                    int duration = baselineSeconds + Random.Shared.Next(-300, 300);
                    if (isAnomalous) duration += 1200;

                    db.Add(new PollRecord
                    {
                        Id = Guid.NewGuid(),
                        RouteId = route.Id,
                        PolledAt = date.AddHours(h).AddMinutes(m),
                        TravelDurationSeconds = duration,
                        DistanceMetres = 18000 + Random.Shared.Next(-200, 200),
                        IsRerouted = isAnomalous && Random.Shared.Next(1, 100) > 50,
                        RawProviderResponse = "{ \"status\": \"OK\", \"simulated\": true }"
                    });
                    polls++;
                }
            }
        }

        await db.SaveChangesAsync();
        logger.LogInformation(
            "[startup] Dev data seeded: admin {Email}, 1 sample route, {Polls} poll records.",
            DevAdminEmail, polls);
    }
}

public sealed record DevAdminLoginCommand : IRequest<DevAdminLoginResult>;

public sealed record DevAdminLoginResult(
    bool IsSuccess,
    AuthResponse? Response,
    string? ErrorCode);

public sealed class DevAdminLoginCommandHandler : IRequestHandler<DevAdminLoginCommand, DevAdminLoginResult>
{
    private readonly TableStorageContext _db;
    private readonly JwtTokenService _jwt;
    private readonly ILogger<DevAdminLoginCommandHandler> _logger;

    public DevAdminLoginCommandHandler(
        TableStorageContext db,
        JwtTokenService jwt,
        ILogger<DevAdminLoginCommandHandler> logger)
    {
        _db = db;
        _jwt = jwt;
        _logger = logger;
    }

    public async Task<DevAdminLoginResult> Handle(DevAdminLoginCommand command, CancellationToken ct)
    {
        User admin = DevAuthExtensions.EnsureDevAdminUser(_db);

        (string accessToken, string refreshToken, DateTimeOffset expiresAt) = _jwt.GenerateTokens(admin);

        admin.RefreshToken = refreshToken;
        admin.RefreshTokenExpiry = DateTimeOffset.UtcNow.AddDays(_jwt.RefreshTokenExpiryDays);
        admin.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Dev admin session created: {Email}", DevAuthExtensions.DevAdminEmail);

        return new DevAdminLoginResult(
            true,
            new AuthResponse(accessToken, refreshToken, expiresAt, admin.Id, admin.Role),
            null);
    }
}
