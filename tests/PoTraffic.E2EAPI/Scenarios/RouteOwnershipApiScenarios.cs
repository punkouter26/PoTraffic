using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using PoTraffic.Shared.DTOs.Routes;
using PoTraffic.Shared.Enums;

namespace PoTraffic.E2EAPI.Scenarios;

/// <summary>
/// IDOR coverage: routes are strictly scoped to their owner — a second
/// authenticated user must never read, mutate, or delete them.
/// </summary>
public sealed class RouteOwnershipApiScenarios
{
    [SkipUnlessApiReady]
    public async Task RoutesOfUserA_AreInvisibleToUserB()
    {
        (HttpClient userA, _) = await ApiSessionFactory.CreateGuestSessionAsync();
        (HttpClient userB, _) = await ApiSessionFactory.CreateGuestSessionAsync();
        using HttpClient _a = userA;
        using HttpClient _b = userB;

        // User A creates a route.
        // MockTrafficProvider geocodes "Mock…" addresses to distinct coordinates.
        HttpResponseMessage createResp = await userA.PostAsJsonAsync("/api/routes",
            new CreateRouteRequest("Mock Origin, Testville", "Waterloo Station, London", RouteProvider.GoogleMaps));
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        RouteDto route = (await createResp.Content.ReadFromJsonAsync<RouteDto>())!;

        // User B cannot see it in their list…
        PagedResult<RouteDto>? listB = await userB.GetFromJsonAsync<PagedResult<RouteDto>>("/api/routes?page=1&pageSize=100");
        listB!.Items.Should().NotContain(r => r.Id == route.Id, "routes must be scoped to their owner");

        // …cannot delete it…
        (await userB.DeleteAsync($"/api/routes/{route.Id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "cross-user delete must behave as not-found");

        // …and user A still owns it.
        PagedResult<RouteDto>? listA = await userA.GetFromJsonAsync<PagedResult<RouteDto>>("/api/routes?page=1&pageSize=100");
        listA!.Items.Should().Contain(r => r.Id == route.Id);

        // Cleanup.
        (await userA.DeleteAsync($"/api/routes/{route.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [SkipUnlessApiReady]
    public async Task Quota_IsReported_ForAuthenticatedUser()
    {
        (HttpClient client, _) = await ApiSessionFactory.CreateGuestSessionAsync();
        using HttpClient disposer = client;

        HttpResponseMessage resp = await client.GetAsync("/api/account/quota");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("dailyLimit").And.Contain("usedToday");
    }
}
