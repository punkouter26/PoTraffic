using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Api.Infrastructure.Security;

namespace PoTraffic.Api.Infrastructure.Testing;

// Factory pattern — conditionally registers test infrastructure endpoints
// These endpoints are NEVER available in Production.

public static class TestingEndpoints
{
    private sealed record DevLoginRequest(string Email, string Role);
    private sealed record DevLoginResponse(string Token);
    private sealed record SeedRequest(string Scenario);
    private sealed record SeedAdminResponse(string Email, string Password);
    private sealed record SeedRouteRequest(string UserEmail, string OriginAddress, string DestinationAddress, int Provider);
    private sealed record SeedRouteResponse(Guid RouteId, string OriginAddress, string DestinationAddress);

    // Marker nested type used as ILogger<T> category
    private sealed class LogCategory;
    public static IEndpointRouteBuilder MapTestingEndpoints(
        this IEndpointRouteBuilder app,
        IWebHostEnvironment env)
    {
        // Guard: only expose in the dedicated Testing environment.
        // Production, Development, and Staging must never serve these endpoints.
        if (!env.IsEnvironment("Testing"))
            return app;

        RouteGroupBuilder group = app.MapGroup("/e2e").WithTags("E2E");

        // POST /e2e/dev-login — issues a JWT for any email/role without a password check
        group.MapPost("/dev-login", DevLogin).AllowAnonymous();

        // POST /e2e/seed — runs a named seeding scenario
        group.MapPost("/seed", Seed).AllowAnonymous();

        // POST /e2e/seed-admin — ensures a known admin user exists; returns credentials
        group.MapPost("/seed-admin", SeedAdmin).AllowAnonymous();

        // POST /e2e/seed-route — creates a route directly in DB for a given user email
        group.MapPost("/seed-route", SeedRoute).AllowAnonymous();

        return app;
    }

    private static IResult DevLogin(
        [FromBody] DevLoginRequest request,
        IConfiguration configuration)
    {
        JwtConfiguration? jwtConfig = configuration
            .GetSection("Jwt")
            .Get<JwtConfiguration>();

        if (jwtConfig is null)
            return Results.Problem("JWT configuration missing.");

        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(jwtConfig.Key));
        SigningCredentials creds = new(key, SecurityAlgorithms.HmacSha256);

        List<Claim> claims =
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Email, request.Email),
            new Claim("role", request.Role),
            new Claim("e2e", "true")
        ];

        JwtSecurityToken token = new(
            issuer:   jwtConfig.Issuer,
            audience: jwtConfig.Audience,
            claims:   claims,
            expires:  DateTime.UtcNow.AddMinutes(jwtConfig.ExpiryMinutes),
            signingCredentials: creds);

        string tokenString = new JwtSecurityTokenHandler().WriteToken(token);
        return Results.Ok(new DevLoginResponse(tokenString));
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
    /// Returns the credentials so tests don't hard-code them.
    /// </summary>
    private static async Task<IResult> SeedAdmin(
        PoTrafficDbContext db,
        ILogger<LogCategory> logger,
        CancellationToken ct)
    {
        const string adminEmail    = "admin@potraffic.dev";
        const string adminPassword = "Admin123!";

        bool exists = await db.Set<User>().AnyAsync(u => u.Email == adminEmail, ct);
        if (!exists)
        {
            db.Set<User>().Add(new User
            {
                Id                     = Guid.NewGuid(),
                Email                  = adminEmail,
                PasswordHash           = BCrypt.Net.BCrypt.HashPassword(adminPassword),
                Locale                 = "en-US",
                Role                   = "Administrator",
                IsEmailVerified        = true,
                EmailVerificationToken = null,
                CreatedAt              = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);
            logger.LogInformation("[E2E] Admin user {Email} created.", adminEmail);
        }

        return Results.Ok(new SeedAdminResponse(adminEmail, adminPassword));
    }

    /// <summary>
    /// Creates a Route row directly in the database for a given user email.
    /// Bypasses geocoding — intended for E2E tests where provider stubs return null.
    /// </summary>
    private static async Task<IResult> SeedRoute(
        [FromBody] SeedRouteRequest request,
        PoTrafficDbContext db,
        ILogger<LogCategory> logger,
        CancellationToken ct)
    {
        User? user = await db.Set<User>().FirstOrDefaultAsync(u => u.Email == request.UserEmail, ct);
        if (user is null)
            return Results.NotFound(new { error = $"User '{request.UserEmail}' not found." });

        // T105: Idempotent - return existing route if user already has it to avoid 409/duplicate errors
        EntityRoute? existing = await db.Set<EntityRoute>().FirstOrDefaultAsync(r => 
            r.UserId == user.Id && 
            r.OriginAddress == request.OriginAddress && 
            r.DestinationAddress == request.DestinationAddress && 
            r.Provider == request.Provider &&
            r.MonitoringStatus != 2, ct);

        if (existing != null)
        {
            logger.LogInformation("[E2E] Route {RouteId} already exists for user {Email}.", existing.Id, user.Email);
            return Results.Ok(new SeedRouteResponse(existing.Id, existing.OriginAddress, existing.DestinationAddress));
        }

        var route = new EntityRoute
        {
            Id                     = Guid.NewGuid(),
            UserId                 = user.Id,
            OriginAddress          = request.OriginAddress,
            OriginCoordinates      = "37.4220,-122.0841",  // fake coords — E2E only
            DestinationAddress     = request.DestinationAddress,
            DestinationCoordinates = "37.3318,-122.0312",  // fake coords — E2E only
            Provider               = request.Provider,
            MonitoringStatus       = 0,
            CreatedAt              = DateTimeOffset.UtcNow
        };

        db.Set<EntityRoute>().Add(route);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("[E2E] Seeded route {RouteId} for user {Email}.", route.Id, user.Email);
        return Results.Ok(new SeedRouteResponse(route.Id, route.OriginAddress, route.DestinationAddress));
    }
}
