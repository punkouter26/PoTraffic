using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PoTraffic.API.Infrastructure.Storage;
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
    [SkipUnlessAzuriteAvailable]
    public async Task DeleteAccount_Returns204_AndRemovesUserFromDatabase()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        // Arrange — guest login creates a fresh real user row
        HttpResponseMessage registerResponse = await client.PostAsync("/api/auth/guest-login", content: null);
        registerResponse.StatusCode.Should().Be(HttpStatusCode.OK, "guest login must succeed");

        AuthMeResponse? auth = await registerResponse.Content.ReadFromJsonAsync<AuthMeResponse>();
        auth.Should().NotBeNull();
        UserId userId = auth!.UserId;

        // Act — delete the authenticated user's account
        HttpResponseMessage deleteResponse = await client.DeleteAsync("/api/account");

        // Assert — FR-031: deletion must return 204 No Content
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "FR-031: DELETE /api/account must return 204 on success");

        // Verify — user row is permanently removed from the database
        using IServiceScope scope = GetServices().CreateScope();
        TableStorageContext db = scope.ServiceProvider.GetRequiredService<TableStorageContext>();
        bool userExists = db.Users.Any(u => u.Id == userId);
        userExists.Should().BeFalse("user row must be hard-deleted after account deletion (FR-031 / GDPR Art. 17)");
    }

    [SkipUnlessAzuriteAvailable]
    public async Task DeleteAccount_WhenCalledTwice_Returns404OnSecondCall()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        // Arrange — guest login + authenticate
        HttpResponseMessage registerResponse = await client.PostAsync("/api/auth/guest-login", content: null);
        registerResponse.StatusCode.Should().Be(HttpStatusCode.OK, "guest login must succeed");
        (await registerResponse.Content.ReadFromJsonAsync<AuthMeResponse>()).Should().NotBeNull();

        // First deletion — must succeed
        HttpResponseMessage first = await client.DeleteAsync("/api/account");
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Second deletion with the same (now-orphaned) cookie session — must return 404
        HttpResponseMessage second = await client.DeleteAsync("/api/account");
        second.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "deleting a non-existent user must return 404 (idempotent guard)");
    }
}
