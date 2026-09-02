// filepath: tests/PoTraffic.E2ETests/Api/Scenarios/RouteCreationSchedulingApiScenarios.cs
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

namespace PoTraffic.E2ETests.Api.Scenarios;

/// <summary>
/// End-to-end scenarios that create a route + monitoring window, start monitoring,
/// then verify a poll travels the whole path — HTTP in, provider call, PollRecord and
/// session statistics out — against the live host.
///
/// <para>
/// The poll is requested explicitly through <c>/e2e/execute-poll</c> rather than waited for.
/// Testing registers a no-op scheduler and no background worker (see
/// <c>BackgroundSchedulerServiceExtensions</c>), so waiting for one to happen on its own
/// waits forever; this scenario previously sat in an eleven-minute timeout and then failed.
/// The scheduler's cadence rules are covered where they can be tested deterministically —
/// <c>PollRouteJob.IsWithinWindow</c> and <c>NextWindowStart</c> in unit tests, the chain
/// itself in the integration tier.
/// </para>
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
    public async Task CreateRouteAndStartMonitoring_RecordsPollAgainstActiveSession()
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

            // ── Act 4: run one poll on the live host
            await ExecutePollAsync(client, route.Id);

            // ── Assert: the poll landed on the session the start created
            SessionDto? activeSession = await GetActiveSessionAsync(client, route.Id);

            Assert.NotNull(activeSession);
            Assert.Equal(SessionState.Active, activeSession!.State);
            Assert.True(activeSession.PollCount >= 1,
                $"ExecutePollCommand should have recorded a sample; PollCount was {activeSession.PollCount}.");
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

    /// <summary>Asks the live Testing host to run one poll for the route, synchronously.</summary>
    private static async Task ExecutePollAsync(HttpClient client, RouteId routeId)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/e2e/execute-poll", new ExecutePollBody(routeId), ApiJsonContext.Default.ExecutePollBody);

        Assert.True(response.IsSuccessStatusCode,
            $"POST /e2e/execute-poll failed: {(int)response.StatusCode} {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<SessionDto?> GetActiveSessionAsync(HttpClient client, RouteId routeId)
    {
        List<SessionDto>? sessions = await client.GetFromJsonAsync(
            $"/api/routes/{routeId}/sessions", ApiJsonContext.Default.ListSessionDto);

        return sessions?.FirstOrDefault(s => s.State == SessionState.Active);
    }

    /// <summary>Body for <c>/e2e/execute-poll</c>. Public so the source-generated
    /// <see cref="ApiJsonContext"/> can expose its type info.</summary>
    public sealed record ExecutePollBody(RouteId RouteId);
}