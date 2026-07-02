using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.IntegrationTests.Helpers;
using PoTraffic.Shared.DTOs.Auth;
using PoTraffic.Shared.DTOs.Routes;

namespace PoTraffic.IntegrationTests.Features.Routes;

/// <summary>
/// Integration tests for POST /api/routes and POST /api/routes/{id}/windows.
/// Verifies route creation persists to DB and monitoring window creation succeeds.
/// Uses real user registration to satisfy FK constraints on Routes.UserId.
/// </summary>
public sealed class CreateRouteIntegrationTests : BaseIntegrationTest
{
    /// <summary>Helper — guest-logs-in (creates a real user row); the factory client keeps the session cookie.</summary>
    private static async Task<AuthMeResponse> RegisterAndAuthenticateAsync(HttpClient client)
    {
        HttpResponseMessage resp = await client.PostAsync("/api/auth/guest-login", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "guest login must succeed");
        return (await resp.Content.ReadFromJsonAsync<AuthMeResponse>())!;
    }

    [SkipUnlessAzuriteAvailable]
    public async Task PostRoutes_CreatesRouteRow()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        // Arrange — real user via guest login
        await RegisterAndAuthenticateAsync(client);

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/routes", new
        {
            OriginAddress = "Baker Street, London",
            DestinationAddress = "Waterloo Station, London",
            Provider = 0, // GoogleMaps
            StartTime = "08:00",
            EndTime = "10:00",
            DaysOfWeekMask = 31 // Mon-Fri
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        RouteDto? body = await response.Content.ReadFromJsonAsync<RouteDto>();
        body.Should().NotBeNull();
        body!.Id.Should().NotBeEmpty();
        body.Windows.Should().HaveCount(1, "initial monitoring window must be created inline");
        body.Windows[0].StartTime.Should().Be("08:00");
        body.Windows[0].EndTime.Should().Be("10:00");

        // Verify row exists in DB
        using IServiceScope scope = GetServices().CreateScope();
        TableStorageContext db = scope.ServiceProvider.GetRequiredService<TableStorageContext>();
        var route = db.Routes.FirstOrDefault(x => x.Id == body.Id);
        route.Should().NotBeNull("route must be persisted to database");

        bool windowExists = db.MonitoringWindows.Any(w => w.RouteId == body.Id);
        windowExists.Should().BeTrue("monitoring window must be persisted to database");
    }

    [SkipUnlessAzuriteAvailable]
    public async Task PostRoutesWindows_CreatesMonitoringWindowRow()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        // Arrange — real user + create route (note: CreateRouteCommand defaults to 07:00-09:00 window)
        await RegisterAndAuthenticateAsync(client);

        HttpResponseMessage routeResp = await client.PostAsJsonAsync("/api/routes", new
        {
            OriginAddress = "Oxford Circus, London",
            DestinationAddress = "Victoria Station, London",
            Provider = 0
        });
        routeResp.StatusCode.Should().Be(HttpStatusCode.Created, "route must be created first");
        RouteDto? route = await routeResp.Content.ReadFromJsonAsync<RouteDto>();

        // The route creation always provisions a default 07:00-09:00 window (CreateRouteCommand defaults).
        // Delete it so we can verify a fresh window can be added via POST /windows.
        if (route!.Windows is { Count: > 0 })
        {
            Guid existingWindowId = route.Windows[0].Id;
            HttpResponseMessage deleteResp = await client.DeleteAsync(
                $"/api/routes/{route.Id}/windows/{existingWindowId}");
            deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
                "existing default window must be deleted before adding a new one");
        }

        // Act — create monitoring window
        HttpResponseMessage windowResp = await client.PostAsJsonAsync(
            $"/api/routes/{route!.Id}/windows", new
            {
                StartTime = "07:00:00",
                EndTime = "09:00:00",
                DaysOfWeekMask = 31 // Mon-Fri (bits 0-4)
            });

        // Assert
        windowResp.StatusCode.Should().Be(HttpStatusCode.Created,
            "monitoring window creation must return 201");

        // Verify in DB
        using IServiceScope scope = GetServices().CreateScope();
        TableStorageContext db = scope.ServiceProvider.GetRequiredService<TableStorageContext>();
        bool exists = db.MonitoringWindows.Any(w => w.RouteId == route.Id && w.IsActive);
        exists.Should().BeTrue("monitoring window must be persisted to database");
    }
}
