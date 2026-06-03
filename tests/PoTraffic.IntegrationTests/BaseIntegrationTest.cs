using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PoTraffic.Api.Features.Auth;
using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.IntegrationTests.Helpers;
using PoTraffic.Shared.Enums;
using PoTraffic.Api.Infrastructure.Storage;

namespace PoTraffic.IntegrationTests;

/// <summary>
/// Base class for all integration tests.
/// Spins up a <see cref="WebApplicationFactory{Program}"/> with the Testing environment.
/// The in-memory <see cref="TableStorageContext"/> provides persistence without external dependencies.
/// </summary>
public abstract class BaseIntegrationTest : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public Task InitializeAsync()
    {
        // Suppress Azure Key Vault loading regardless of ASPNETCORE_ENVIRONMENT.
        Environment.SetEnvironmentVariable("AzureKeyVault__VaultUri", string.Empty);
        Environment.SetEnvironmentVariable("KeyVault__Uri", string.Empty);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");

                builder.ConfigureServices(services =>
                {
                    services.AddKeyedScoped<ITrafficProvider, FakeTrafficProvider>(RouteProvider.GoogleMaps);
                    services.AddKeyedScoped<ITrafficProvider, FakeTrafficProvider>(RouteProvider.TomTom);
                    services.AddScoped<IExternalIdentityProvider>(_ => new FakeExternalIdentityProvider("google"));
                    services.AddScoped<IExternalIdentityProvider>(_ => new FakeExternalIdentityProvider("microsoft"));
                });

                ConfigureHost(builder);
            });

        // Warm up the host so the DI container is built before tests run
        _ = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();

        Environment.SetEnvironmentVariable("AzureKeyVault__VaultUri", null);
        Environment.SetEnvironmentVariable("KeyVault__Uri", null);

        return Task.CompletedTask;
    }

    // ── Protected helpers ─────────────────────────────────────────────────────

    protected virtual void ConfigureHost(IWebHostBuilder builder) { }

    protected HttpClient CreateClient()
    {
        if (_factory is null)
            throw new InvalidOperationException("Factory not yet initialised. Call InitializeAsync first.");

        return _factory.CreateClient();
    }

    protected HttpClient CreateClientNoRedirect()
    {
        if (_factory is null)
            throw new InvalidOperationException("Factory not yet initialised. Call InitializeAsync first.");

        return _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    protected IServiceProvider GetServices()
    {
        if (_factory is null)
            throw new InvalidOperationException("Factory not yet initialised. Call InitializeAsync first.");

        return _factory.Services;
    }

    /// <summary>
    /// Seeds default configuration rows in the in-memory <see cref="TableStorageContext"/>.
    /// Call this from a test that requires cost/quota configuration to be present.
    /// </summary>
    protected void SeedDefaultConfigurations()
    {
        if (_factory is null)
            throw new InvalidOperationException("Factory not yet initialised.");

        using IServiceScope scope = _factory.Services.CreateScope();
        TableStorageContext ctx = scope.ServiceProvider.GetRequiredService<TableStorageContext>();
        ctx.SeedDefaultConfigurationsIfMissing();
    }

    /// <summary>
    /// Returns the singleton <see cref="TableStorageContext"/> from the test host.
    /// Use this to seed test data directly.
    /// </summary>
    protected TableStorageContext GetDbContext()
    {
        if (_factory is null)
            throw new InvalidOperationException("Factory not yet initialised.");

        return _factory.Services.GetRequiredService<TableStorageContext>();
    }

    /// <summary>
    /// No-op — the in-memory <see cref="TableStorageContext"/> does not require migrations.
    /// Kept so existing integration tests compile without change.
    /// </summary>
    protected Task ApplyMigrationsAsync() => Task.CompletedTask;
}
