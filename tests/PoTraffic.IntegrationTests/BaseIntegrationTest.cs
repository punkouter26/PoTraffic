using System.Linq.Expressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoTraffic.API.Features.Auth;
using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.API.Infrastructure.Scheduling;
using PoTraffic.API.Infrastructure.Security;
using PoTraffic.IntegrationTests.Helpers;
using PoTraffic.IntegrationTests.Infrastructure;
using PoTraffic.Shared.Enums;
using PoTraffic.API.Infrastructure.Storage;

namespace PoTraffic.IntegrationTests;

/// <summary>
/// Base class for all integration tests.
/// Spins up a <see cref="WebApplicationFactory{Program}"/> with the Testing environment.
/// Azurite is owned by Testcontainers so tests never depend on a manually started emulator.
///
/// <para>
/// Because <see cref="InitializeAsync"/> starts that container, every test method on a
/// subclass must be marked <c>[SkipUnlessAzuriteAvailable]</c> rather than <c>[Fact]</c> —
/// a plain <c>[Fact]</c> hard-fails with <c>DockerUnavailableException</c> on a machine
/// without Docker instead of skipping alongside the rest of the suite.
/// </para>
/// </summary>
public abstract class BaseIntegrationTest : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        // Suppress Azure Key Vault loading regardless of ASPNETCORE_ENVIRONMENT.
        Environment.SetEnvironmentVariable("AzureKeyVault__VaultUri", string.Empty);
        Environment.SetEnvironmentVariable("KeyVault__Uri", string.Empty);

        // CI/CD rule #3 — lifecycle-managed Testcontainers. The container is
        // shared for the duration of the run and explicitly torn down by
        // SCRIPTS/run-tests.ps1 (or DisposeInstanceAsync() on AppDomain exit).
        AzuriteTestContainer azurite = await AzuriteTestContainer.GetInstanceAsync();
        string tableStorageConnectionString = azurite.ConnectionString;

        // Point storage at the Azurite testcontainer via ENVIRONMENT VARIABLES.
        // These are read by WebApplication.CreateBuilder() before Program.cs runs
        // AddTableStorageServices, so the Azurite connection string is visible when
        // the TableServiceClient is selected. ConfigureAppConfiguration (below) is
        // merged into the final IConfiguration only AFTER service registration in
        // minimal hosting — too late for client selection, which would otherwise
        // fall through to the production Managed Identity path (169.254.169.254 IMDS
        // → socket-unreachable 500 on every storage write). Same value for every
        // test (one shared container), so concurrent sets are idempotent; left set
        // for the process lifetime to avoid a teardown race with parallel classes.
        Environment.SetEnvironmentVariable("ConnectionStrings__TableStorage", tableStorageConnectionString);
        Environment.SetEnvironmentVariable("AzureTable__UseManagedIdentity", "false");

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:TableStorage"] = tableStorageConnectionString,
                        ["AzureTable:UseManagedIdentity"] = "false"
                    });
                });

                builder.ConfigureServices(services =>
                {
                    services.AddKeyedScoped<ITrafficProvider, FakeTrafficProvider>(RouteProvider.GoogleMaps);
                    services.AddKeyedScoped<ITrafficProvider, FakeTrafficProvider>(RouteProvider.TomTom);

                    // CI/CD rule #4 — strip real Microsoft OAuth network traffic.
                    // 1) Replace the MicrosoftExternalIdentityProvider with an in-process fake.
                    // 2) Strip the HttpClient<MicrosoftExternalIdentityProvider>() primary handler
                    //    by registering the MockExternalAuthDelegatingHandler ahead of it.
                    services.RemoveAll<MicrosoftExternalIdentityProvider>();
                    services.AddScoped<IExternalIdentityProvider>(_ => new FakeExternalIdentityProvider("microsoft"));

                    // Register a no-op IJobScheduler for handlers that depend on it
                    // (BackgroundSchedulerService is skipped in Testing env)
                    services.AddSingleton<IJobScheduler>(new NoOpJobScheduler());
                });

                ConfigureHost(builder);
            });

        // Warm up the host so the DI container is built before tests run
        _ = _factory.CreateClient();
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
    /// No-op — the in-memory <see cref="TableStorageContext"/> does not require schema migrations.
    /// Kept so existing integration tests compile without change.
    /// </summary>
    protected Task ApplyMigrationsAsync() => Task.CompletedTask;
}

/// <summary>
/// No-op IJobScheduler for integration tests. Handlers that inject IJobScheduler
/// (e.g. job handlers) can resolve without requiring a running Azurite instance.
/// </summary>
internal sealed class NoOpJobScheduler : IJobScheduler
{
    public string Enqueue(Expression<Func<Task>> job) => "noop-enqueue";
    public string Schedule(Expression<Func<Task>> job, TimeSpan delay) => "noop-schedule";
    public void Cancel(string jobId) { }
    public int CancelPendingPollJobsForRoute(RouteId routeId) => 0;
    public void ScheduleRecurring(string jobId, Func<Task> job, TimeOnly dailyAtUtc) { }
}
