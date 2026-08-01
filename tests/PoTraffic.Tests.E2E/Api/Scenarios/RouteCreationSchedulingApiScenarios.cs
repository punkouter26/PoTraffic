// filepath: tests/PoTraffic.Tests.E2E/Api/Scenarios/RouteCreationSchedulingApiScenarios.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.DTOs.History;
using PoTraffic.Shared.DTOs.Routes;
using PoTraffic.Shared.Enums;
using Xunit;

namespace PoTraffic.Tests.E2E.Api.Scenarios;

/// <summary>
/// End-to-end scenarios that create a route + monitoring window, start monitoring,
/// then verify the BackgroundSchedulerService actually polls the route on the
/// expected cadence (one poll every <see cref="QuotaConstants.PollIntervalMinutes"/>).
///
/// These run against the live Testing-instance at http://localhost:5150. They
/// auto-skip (via <see cref="ApiSkipUnlessReadyAttribute"/>) when no live API
/// is reachable, so they don't block CI on machines without a running instance.
/// </summary>
public sealed class RouteCreationSchedulingApiScenarios
{
    private const string OriginAddressBase = "1600 Amphitheatre Parkway, Mountain View, CA";
    private const string DestinationAddressBase = "1 Infinite Loop, Cupertino, CA";

    // Per-run origin suffix so re-runs of the scenario don't collide with
    // previously created routes (the server enforces uniqueness on origin coords).
    private static readonly string OriginAddress =
        $"{OriginAddressBase} #{Guid.NewGuid():N}";
    private static readonly string DestinationAddress =
        $"{DestinationAddressBase} #{Guid.NewGuid():N}";

    [Fact]
    public async Task CreateRouteAndStartMonitoring_ProducesPollWithinTwoIntervals()
    {
        await ApiSkipUnlessReadyAttribute.ThrowUnlessReadyAsync();

        // ── Arrange: GUEST session ────────────────────────────────────────
        (HttpClient client, string _) = await ApiSessionFactory.CreateGuestSessionAsync();

        try
        {
            // ── Act 1: create route ───────────────────────────────────────
            RouteDto route = await CreateRouteAsync(client);

            // ── Act 2: create a wide-open monitoring window (covers "now")
            MonitoringWindowDto window = await CreateWindowAsync(client, route.Id);

            // ── Act 3: start monitoring
            await StartMonitoringAsync(client, route.Id, window.Id);

            // ── Assert: scheduler picks the route up within 2 × PollInterval
            //              (first poll runs immediately, second within 2× interval)
            int totalSeconds = (QuotaConstants.PollIntervalMinutes * 2 + 1) * 60;
            SessionDto? activeSession = await PollUntilFirstPollAsync(client, route.Id, TimeSpan.FromSeconds(totalSeconds));

            Assert.NotNull(activeSession);
            Assert.Equal(SessionState.Active, activeSession!.State);
            Assert.True(activeSession.PollCount >= 1,
                $"PollRouteJob should have executed at least once; PollCount was {activeSession.PollCount}.");
            Assert.NotNull(activeSession.FirstPollAt);

            // LastPollAt must also be set (executor ticks LastPollAt on each run).
            Assert.NotNull(activeSession.LastPollAt);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task HealthCheck_SchedulerEntryIsHealthy_WhenLiveApiRunning()
    {
        await ApiSkipUnlessReadyAttribute.ThrowUnlessReadyAsync();

        using HttpClient client = ApiSessionFactory.CreateAnonymous();
        HttpResponseMessage response = await client.GetAsync("/health/json");
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"name\":\"scheduler\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"status\":\"Healthy\"", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static async Task<RouteDto> CreateRouteAsync(HttpClient client)
    {
        var request = new CreateRouteRequest(
            OriginAddress: OriginAddress,
            DestinationAddress: DestinationAddress,
            Provider: RouteProvider.GoogleMaps,
            StartTime: DateTime.UtcNow.ToString("HH:mm"),
            EndTime: DateTime.UtcNow.AddHours(2).ToString("HH:mm"),
            DaysOfWeekMask: 0x7F); // every day, ensure window is "active today"

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/routes", request);
        Assert.True(response.IsSuccessStatusCode,
            $"POST /api/routes failed: {(int)response.StatusCode} {response.StatusCode}\n{await response.Content.ReadAsStringAsync()}");

        RouteDto? route = await response.Content.ReadFromJsonAsync(ApiJsonContext.Default.RouteDto);
        Assert.NotNull(route);
        return route!;
    }

    private static async Task<MonitoringWindowDto> CreateWindowAsync(HttpClient client, RouteId routeId)
    {
        // If a window already exists for this route (e.g. from a prior run
        // sharing the same GUEST account), reuse it rather than failing on the
        // "only one active window per route" rule.
        List<MonitoringWindowDto>? existing = await client.GetFromJsonAsync(
            $"/api/routes/{routeId}/windows", ApiJsonContext.Default.ListMonitoringWindowDto);
        MonitoringWindowDto? active = existing?.FirstOrDefault(w => w.IsActive);
        if (active is not null) return active;

        var request = new
        {
            startTime = DateTime.UtcNow.AddMinutes(-1).ToString("HH:mm:00"),
            endTime = DateTime.UtcNow.AddHours(2).ToString("HH:mm:00"),
            daysOfWeekMask = (byte)0x7F
        };

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/routes/{routeId}/windows", request);
        Assert.True(response.IsSuccessStatusCode,
            $"POST /windows failed: {(int)response.StatusCode} {response.StatusCode}\n{await response.Content.ReadAsStringAsync()}");

        List<MonitoringWindowDto>? windows = await client.GetFromJsonAsync(
            $"/api/routes/{routeId}/windows", ApiJsonContext.Default.ListMonitoringWindowDto);
        Assert.NotNull(windows);
        MonitoringWindowDto? window = windows!.FirstOrDefault(w => w.IsActive);
        Assert.NotNull(window);
        return window!;
    }

    private static async Task StartMonitoringAsync(HttpClient client, RouteId routeId, WindowId windowId)
    {
        HttpResponseMessage response = await client.PostAsync(
            $"/api/routes/{routeId}/windows/{windowId}/start", content: null);
        Assert.True(response.IsSuccessStatusCode,
            $"POST /start failed: {(int)response.StatusCode} {response.StatusCode}\n{await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<SessionDto?> PollUntilFirstPollAsync(
        HttpClient client, RouteId routeId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        SessionDto? lastSeen = null;
        while (DateTime.UtcNow < deadline)
        {
            List<SessionDto>? sessions = await client.GetFromJsonAsync(
                $"/api/routes/{routeId}/sessions", ApiJsonContext.Default.ListSessionDto);

            SessionDto? active = sessions?.FirstOrDefault(s => s.State == SessionState.Active);
            if (active is not null)
            {
                lastSeen = active;
                if (active.FirstPollAt is not null)
                    return active;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return lastSeen;
    }
}