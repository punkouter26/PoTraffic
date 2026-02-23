using MediatR;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;


namespace PoTraffic.Api.Features.Admin;

/// <summary>
/// SeedDatabaseCommand — Diagnostic utility for admins to populate the system with
/// synthetic user, route, and historical poll data for testing and demos.
/// </summary>
public sealed record SeedDatabaseCommand(int RouteCount = 3, int DaysOfHistory = 14) : IRequest<SeedDatabaseResult>;

public sealed record SeedDatabaseResult(int UsersCreated, int RoutesCreated, int PollsCreated);

public sealed class SeedDatabaseHandler(
    PoTrafficDbContext db,
    ILogger<SeedDatabaseHandler> logger) 
    : IRequestHandler<SeedDatabaseCommand, SeedDatabaseResult>
{
    public async Task<SeedDatabaseResult> Handle(SeedDatabaseCommand request, CancellationToken ct)
    {
        int usersCreatedCount = 0;
        int routesCreatedCount = 0;
        int pollsCreatedCount = 0;

        // 1. Ensure sample commuter users exist
        var sampleUsers = new[]
        {
            new { Email = "commuter-sample-1@potraffic.dev", Name = "Sample User One" },
            new { Email = "commuter-sample-2@potraffic.dev", Name = "Sample User Two" }
        };

        foreach (var u in sampleUsers)
        {
            User? user = await db.Users.FirstOrDefaultAsync(x => x.Email == u.Email, ct);
            if (user == null)
            {
                user = new User
                {
                    Id                     = Guid.NewGuid(),
                    Email                  = u.Email,
                    PasswordHash           = BCrypt.Net.BCrypt.HashPassword("User123!"),
                    Locale                 = "en-US",
                    Role                   = "Commuter",
                    IsEmailVerified        = true,
                    CreatedAt              = DateTimeOffset.UtcNow
                };
                db.Users.Add(user);
                usersCreatedCount++;
            }

            // 2. Add sample routes for this user if they don't have any
            bool hasRoutes = await db.Routes.AnyAsync(r => r.UserId == user.Id, ct);
            if (!hasRoutes)
            {
                var routes = new[]
                {
                    new { Origin = "501 Sylview Dr, Pasadena, CA", Destination = "456 S Fair Oaks Ave, Pasadena, CA", Provider = 0 },
                    new { Origin = "4451 Telfair Blvd, Camp Springs, MD 20746", Destination = "251 Admiral Cochrane Dr, Annapolis, MD 21401", Provider = 0 },
                    new { Origin = "1600 Amphitheatre Pkwy, Mountain View, CA", Destination = "1 Infinite Loop, Cupertino, CA", Provider = 0 }
                };

                foreach (var rd in routes.Take(request.RouteCount))
                {
                    var route = new EntityRoute
                    {
                        Id                     = Guid.NewGuid(),
                        UserId                 = user.Id,
                        OriginAddress          = rd.Origin,
                        OriginCoordinates      = "34.1478,-118.1445", // dummy coordinates
                        DestinationAddress     = rd.Destination,
                        DestinationCoordinates = "34.1478,-118.1445", // dummy coordinates
                        Provider               = rd.Provider,
                        MonitoringStatus       = 0,
                        CreatedAt              = DateTimeOffset.UtcNow
                    };
                    db.Routes.Add(route);
                    routesCreatedCount++;

                    // 3. Seed historical polls for this route to generate volatility visualizations
                    var random = new Random();
                    // Generate baseline: average of 25 mins (1500s)
                    int baselineSeconds = 1500;

                    for (int d = 0; d < request.DaysOfHistory; d++)
                    {
                        // Skip weekends for commute simulation
                        var date = DateTimeOffset.UtcNow.Date.AddDays(-d);
                        if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                            continue;

                        // Morning Peak (07:00 - 09:00)
                        for (int h = 7; h <= 9; h++)
                        {
                            // 5-minute intervals
                            for (int m = 0; m < 60; m += 5)
                            {
                                var polledAt = date.AddHours(h).AddMinutes(m);
                                
                                // Randomize duration with some "volatility" (high standard deviation)
                                // Standard variation: +/- 5 mins
                                // Rare anomaly: + 20 mins
                                bool isAnomalous = random.Next(1, 100) > 92;
                                int duration = baselineSeconds + random.Next(-300, 300);
                                if (isAnomalous) duration += 1200;

                                db.PollRecords.Add(new PollRecord
                                {
                                    Id                    = Guid.NewGuid(),
                                    RouteId               = route.Id,
                                    PolledAt              = polledAt,
                                    TravelDurationSeconds = duration,
                                    DistanceMetres        = 18000 + random.Next(-200, 200),
                                    IsRerouted            = isAnomalous && random.Next(1, 100) > 50,
                                    RawProviderResponse   = "{ \"status\": \"OK\", \"simulated\": true }"
                                });
                                pollsCreatedCount++;
                            }
                        }
                    }
                }
            }
        }

        await db.SaveChangesAsync(ct);
        
        logger.LogInformation("[Admin] Seeded database: {Users} users, {Routes} routes, {Polls} polls.", 
            usersCreatedCount, routesCreatedCount, pollsCreatedCount);

        return new SeedDatabaseResult(usersCreatedCount, routesCreatedCount, pollsCreatedCount);
    }
}
