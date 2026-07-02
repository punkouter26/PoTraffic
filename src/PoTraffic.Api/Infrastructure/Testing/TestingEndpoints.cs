using Microsoft.AspNetCore.Mvc;
using PoTraffic.Api.Features.Auth;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.Api.Infrastructure.Testing;

public static class TestingEndpoints
{
    private sealed record DevLoginRequest(string Email, string Role);
    private sealed record SeedRequest(string Scenario);
    private sealed record SeedAdminResponse(string Email);
    private sealed record SeedRouteRequest(string UserEmail, string OriginAddress, string DestinationAddress, int Provider);
    private sealed record SeedRouteResponse(Guid RouteId, string OriginAddress, string DestinationAddress);

    // Marker nested type used as ILogger<T> category
    private sealed class LogCategory;
    public static IEndpointRouteBuilder MapTestingEndpoints(
        this IEndpointRouteBuilder app,
        IWebHostEnvironment env)
    {
        // Guard: only expose in the dedicated Testing environment.
        // Development, Production and Staging must never serve these endpoints.
        if (!env.IsEnvironment("Testing"))
            return app;

        RouteGroupBuilder group = app.MapGroup("/e2e").WithTags("E2E");

        // POST /e2e/dev-login — establishes a BFF cookie session for any email/role
        group.MapPost("/dev-login", DevLogin).AllowAnonymous();

        // POST /e2e/seed — runs a named seeding scenario
        group.MapPost("/seed", Seed).AllowAnonymous();

        // POST /e2e/seed-admin — ensures a known admin user exists
        group.MapPost("/seed-admin", SeedAdmin).AllowAnonymous();

        // POST /e2e/seed-route — creates a route directly in DB for a given user email
        group.MapPost("/seed-route", SeedRoute).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> DevLogin(
        [FromBody] DevLoginRequest request,
        HttpContext httpContext,
        TableStorageContext db,
        CancellationToken ct)
    {
        User? user = db.Users.FirstOrDefault(u => u.Email == request.Email);
        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                Email = request.Email,
                Locale = "en-US",
                Role = request.Role,
                AuthProvider = "e2e",
                IsEmailVerified = true,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Add(user);
            await db.SaveChangesAsync(ct);
        }

        await CookieSignIn.SignInAsync(httpContext, user);
        return Results.Ok(new AuthMeResponse(user.Id, user.Email, user.Role, user.AuthProvider));
    }

    private static IResult Seed(
        [FromBody] SeedRequest request,
        ILogger<LogCategory> logger)
    {
        logger.LogInformation("[E2E] Seed scenario requested: {Scenario}", request.Scenario);

        // Scenario runners will be registered here as the E2E suite grows.
        // For now, unknown scenarios are accepted silently so tests can run
        // before all seed scenarios are implemented.
        return Results.NoContent();
    }

    /// <summary>
    /// Ensures a known Administrator user exists in the database.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    private static async Task<IResult> SeedAdmin(
        TableStorageContext db,
        ILogger<LogCategory> logger,
        CancellationToken ct)
    {
        const string adminEmail = "admin@potraffic.dev";

        bool exists = db.Users.Any(u => u.Email == adminEmail);
        if (!exists)
        {
            db.Add(new User
            {
                Id = Guid.NewGuid(),
                Email = adminEmail,
                Locale = "en-US",
                Role = "Administrator",
                AuthProvider = "e2e",
                IsEmailVerified = true,
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);
            logger.LogInformation("[E2E] Admin user {Email} created.", adminEmail);
        }

        return Results.Ok(new SeedAdminResponse(adminEmail));
    }

    /// <summary>
    /// Creates a Route row directly in the database for a given user email.
    /// Bypasses geocoding — intended for E2E tests where provider stubs return null.
    /// </summary>
    private static async Task<IResult> SeedRoute(
        [FromBody] SeedRouteRequest request,
        TableStorageContext db,
        ILogger<LogCategory> logger,
        CancellationToken ct)
    {
        User? user = db.Users.FirstOrDefault(u => u.Email == request.UserEmail);
        if (user is null)
            return Results.NotFound(new { error = $"User '{request.UserEmail}' not found." });

        // T105: Idempotent - return existing route if user already has it to avoid 409/duplicate errors
        EntityRoute? existing = db.Routes.FirstOrDefault(r =>
            r.UserId == user.Id &&
            r.OriginAddress == request.OriginAddress &&
            r.DestinationAddress == request.DestinationAddress &&
            r.Provider == request.Provider &&
            r.MonitoringStatus != 2);

        if (existing != null)
        {
            logger.LogInformation("[E2E] Route {RouteId} already exists for user {Email}.", existing.Id, user.Email);
            return Results.Ok(new SeedRouteResponse(existing.Id, existing.OriginAddress, existing.DestinationAddress));
        }

        var route = new EntityRoute
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            OriginAddress = request.OriginAddress,
            OriginCoordinates = "37.4220,-122.0841",  // fake coords — E2E only
            DestinationAddress = request.DestinationAddress,
            DestinationCoordinates = "37.3318,-122.0312",  // fake coords — E2E only
            Provider = request.Provider,
            MonitoringStatus = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Add(route);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("[E2E] Seeded route {RouteId} for user {Email}.", route.Id, user.Email);
        return Results.Ok(new SeedRouteResponse(route.Id, route.OriginAddress, route.DestinationAddress));
    }
}
