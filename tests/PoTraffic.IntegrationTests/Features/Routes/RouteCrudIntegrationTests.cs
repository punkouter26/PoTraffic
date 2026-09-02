using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using PoTraffic.IntegrationTests.Helpers;
using PoTraffic.Shared.DTOs.Auth;
using PoTraffic.Shared.DTOs.Routes;

namespace PoTraffic.IntegrationTests.Features.Routes;

/// <summary>
/// Integration tests for Route CRUD and Check Now operations.
/// FR-016: Check Now does not persist a PollRecord.
/// </summary>
public sealed class RouteCrudIntegrationTests : BaseIntegrationTest
{
    [SkipUnlessAzuriteAvailable]
    public async Task RouteCrud_CreateGetDelete_FullLifecycle()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        // Arrange — guest login creates a real user row to satisfy FK constraints
        HttpResponseMessage guestResp = await client.PostAsync("/api/auth/guest-login", content: null);
        guestResp.StatusCode.Should().Be(HttpStatusCode.OK, "guest login must succeed");
        (await guestResp.Content.ReadFromJsonAsync<AuthMeResponse>()).Should().NotBeNull();

        // Act 1 — GET routes (empty initially)
        HttpResponseMessage getEmptyResp = await client.GetAsync("/api/routes?page=1&pageSize=10");
        getEmptyResp.StatusCode.Should().Be(HttpStatusCode.OK);
        PagedResult<RouteDto>? emptyPage = await getEmptyResp.Content.ReadFromJsonAsync<PagedResult<RouteDto>>();
        emptyPage!.TotalCount.Should().Be(0, "new user should have no routes");

        // Act 2 — POST route
        HttpResponseMessage createResp = await client.PostAsJsonAsync("/api/routes", new
        {
            OriginAddress = "Baker Street, London",
            DestinationAddress = "Waterloo Station, London",
            Provider = 0 // GoogleMaps
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created, "route creation must return 201");
        RouteDto? created = await createResp.Content.ReadFromJsonAsync<RouteDto>();
        created.Should().NotBeNull();
        created!.Id.IsEmpty.Should().BeFalse();

        // Act 3 — GET routes (should have 1)
        HttpResponseMessage getOneResp = await client.GetAsync("/api/routes?page=1&pageSize=10");
        PagedResult<RouteDto>? onePage = await getOneResp.Content.ReadFromJsonAsync<PagedResult<RouteDto>>();
        onePage!.TotalCount.Should().Be(1);

        // Act 4 — DELETE route (soft-delete)
        HttpResponseMessage deleteResp = await client.DeleteAsync($"/api/routes/{created.Id}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent, "route deletion must return 204");

        // Act 5 — GET routes (should be empty again as Deleted routes are excluded)
        HttpResponseMessage getFinalResp = await client.GetAsync("/api/routes?page=1&pageSize=10");
        PagedResult<RouteDto>? finalPage = await getFinalResp.Content.ReadFromJsonAsync<PagedResult<RouteDto>>();
        finalPage!.TotalCount.Should().Be(0, "deleted routes must not appear in GET results");
    }
}
