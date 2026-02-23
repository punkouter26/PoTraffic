using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.IntegrationTests.Helpers;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.IntegrationTests.Features.Account;

/// <summary>
/// Integration tests for DELETE /api/account (FR-031).
/// Verifies that a user's account row and all associated data are hard-deleted
/// (GDPR Art. 17) when the authenticated user requests account deletion.
/// </summary>
public sealed class DeleteAccountIntegrationTests : BaseIntegrationTest
{
    [SkipUnlessDockerAvailable]
    public async Task DeleteAccount_Returns204_AndRemovesUserFromDatabase()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        // Arrange — register a fresh user
        var registerBody = new
        {
            Email    = "delete-me@test.invalid",
            Password = "Test!P@ss99",
            Locale   = "en-IE"
        };
        HttpResponseMessage registerResponse = await client.PostAsJsonAsync("/api/auth/register", registerBody);
        registerResponse.StatusCode.Should().Be(HttpStatusCode.Created, "registration must succeed");

        AuthResponse? auth = await registerResponse.Content.ReadFromJsonAsync<AuthResponse>();
        auth.Should().NotBeNull();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        Guid userId = auth.UserId;

        // Act — delete the authenticated user's account
        HttpResponseMessage deleteResponse = await client.DeleteAsync("/api/account");

        // Assert — FR-031: deletion must return 204 No Content
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "FR-031: DELETE /api/account must return 204 on success");

        // Verify — user row is permanently removed from the database
        using IServiceScope scope = GetServices().CreateScope();
        PoTrafficDbContext db = scope.ServiceProvider.GetRequiredService<PoTrafficDbContext>();
        bool userExists = db.Users.Any(u => u.Id == userId);
        userExists.Should().BeFalse("user row must be hard-deleted after account deletion (FR-031 / GDPR Art. 17)");
    }

    [SkipUnlessDockerAvailable]
    public async Task DeleteAccount_WhenCalledTwice_Returns404OnSecondCall()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        // Arrange — register + authenticate
        var registerBody = new
        {
            Email    = "delete-twice@test.invalid",
            Password = "Test!P@ss99",
            Locale   = "en-IE"
        };
        HttpResponseMessage registerResponse = await client.PostAsJsonAsync("/api/auth/register", registerBody);
        AuthResponse? auth = await registerResponse.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        // First deletion — must succeed
        HttpResponseMessage first = await client.DeleteAsync("/api/account");
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Second deletion with same (now-gone) JWT — must return 404
        HttpResponseMessage second = await client.DeleteAsync("/api/account");
        second.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "deleting a non-existent user must return 404 (idempotent guard)");
    }
}
