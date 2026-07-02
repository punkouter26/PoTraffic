using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PoTraffic.Api.Infrastructure.Storage;

using PoTraffic.IntegrationTests.Helpers;
using PoTraffic.Shared.DTOs.Admin;

namespace PoTraffic.IntegrationTests.Features.Admin;

/// <summary>
/// Integration tests for GET /api/admin/configuration sensitive-value masking (FR-026).
/// Verifies that IsSensitive=true values are masked in the API response
/// and that IsSensitive=false values are returned verbatim.
/// </summary>
public sealed class SensitiveMaskingIntegrationTests : BaseIntegrationTest
{
    // Template Method — set Testing environment so /e2e/dev-login is registered.
    protected override void ConfigureHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Testing");

    [SkipUnlessAzuriteAvailable]
    public async Task GetConfiguration_SensitiveValues_AreMasked()
    {
        await ApplyMigrationsAsync();

        // Arrange — seed a sensitive and a non-sensitive config entry via DB
        using (IServiceScope scope = GetServices().CreateScope())
        {
            TableStorageContext db = scope.ServiceProvider.GetRequiredService<TableStorageContext>();
            db.AddRange(new SystemConfiguration[]
            {
                new SystemConfiguration { Key = "test.api.key", Value = "super-secret-value-1234", IsSensitive = true, Description = "Test sensitive" },
                new SystemConfiguration { Key = "test.poll.interval", Value = "60", IsSensitive = false, Description = "Test non-sensitive" }
            });
            db.SaveChangesAsync().GetAwaiter().GetResult();
        }

        // Log in as admin via the testing endpoint
        using HttpClient client = CreateClient();
        HttpResponseMessage loginResponse = await client.PostAsJsonAsync("/e2e/dev-login", new { email = "admin@test.invalid", role = "Administrator" });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/admin/configuration");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<SystemConfigDto>? configs = await response.Content.ReadFromJsonAsync<List<SystemConfigDto>>();
        configs.Should().NotBeNull();

        // Assert — FR-026: sensitive value is masked (first-2 + **** + last-2)
        SystemConfigDto? sensitiveConfig = configs!.FirstOrDefault(c => c.Key == "test.api.key");
        sensitiveConfig.Should().NotBeNull("seeded sensitive config must be returned");
        sensitiveConfig!.IsSensitive.Should().BeTrue();
        sensitiveConfig.Value.Should()
            .NotBe("super-secret-value-1234", "FR-026: plain-text sensitive value must not be exposed")
            .And.Contain("****", "FR-026: masked values must include the **** sentinel");

        // Assert — non-sensitive value is returned verbatim
        SystemConfigDto? plainConfig = configs!.FirstOrDefault(c => c.Key == "test.poll.interval");
        plainConfig.Should().NotBeNull("seeded non-sensitive config must be returned");
        plainConfig!.IsSensitive.Should().BeFalse();
        plainConfig.Value.Should().Be("60", "non-sensitive values must be returned unchanged");
    }

    // Local DTO — mirrors the /e2e/dev-login JSON response shape
}
