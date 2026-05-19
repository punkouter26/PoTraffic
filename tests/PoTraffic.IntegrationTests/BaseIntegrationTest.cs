using DotNet.Testcontainers.Builders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PoTraffic.Api.Features.Auth;
using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.IntegrationTests.Helpers;
using PoTraffic.Shared.Enums;
using Testcontainers.MsSql;
using PoTraffic.Api.Infrastructure.Data;

namespace PoTraffic.IntegrationTests;

/// <summary>
/// Base class for all integration tests.
/// Spins up a real SQL Server–compatible container per test class,
/// applies EF Core migrations, and exposes a pre-configured <see cref="HttpClient"/>.
/// Uses Azure SQL Edge because this environment runs Docker via QEMU emulation;
/// only ARM64-capable images (azure-sql-edge) run without SIGSEGV.
/// Custom TCP wait strategy bypasses the built-in sqlcmd probe, which is
/// absent from Azure SQL Edge images.
/// </summary>
public abstract class BaseIntegrationTest : IAsyncLifetime
{
    private const string SaPassword = "Testing!P@ssw0rd";

    private readonly MsSqlContainer _dbContainer = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/azure-sql-edge:latest")
        .WithPassword(SaPassword)
        .WithWaitStrategy(
            Wait.ForUnixContainer()
                .UntilPortIsAvailable(1433))
        .Build();

    private WebApplicationFactory<Program>? _factory;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        // Suppress Azure Key Vault loading regardless of ASPNETCORE_ENVIRONMENT.
        // In CI (no Azure credentials) AddAzureKeyVault would throw CredentialUnavailableException
        // during builder.Build(), causing WebApplicationFactory to report "entry point exited".
        // Environment variables override appsettings.*.json files and are read by
        // WebApplication.CreateBuilder before any WithWebHostBuilder callbacks execute.
        Environment.SetEnvironmentVariable("AzureKeyVault__VaultUri", string.Empty);
        Environment.SetEnvironmentVariable("KeyVault__Uri", string.Empty);

        await _dbContainer.StartAsync();

        // Wait for SQL Server engine readiness (port open ≠ engine ready)
        await WaitForSqlReadinessAsync(_dbContainer.GetConnectionString());

        // Override the connection string BEFORE creating WebApplicationFactory.
        // Service registrations (AddDataServices, AddHangfireServices) read ConnectionStrings:Default
        // from builder.Configuration at registration time — before builder.Build() is called.
        // WithWebHostBuilder ConfigureAppConfiguration callbacks run only during builder.Build()
        // and therefore arrive too late to override the CI env var for service registrations.
        // Setting the env var here ensures WebApplication.CreateBuilder picks up the correct
        // Testcontainers connection string when it loads configuration.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", _dbContainer.GetConnectionString());

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {

                // Replace real traffic providers with a fake that returns
                // deterministic geocode coordinates (avoids GEOCODE_FAILED 422)
                builder.ConfigureServices(services =>
                {
                    services.AddKeyedScoped<ITrafficProvider, FakeTrafficProvider>(RouteProvider.GoogleMaps);
                    services.AddKeyedScoped<ITrafficProvider, FakeTrafficProvider>(RouteProvider.TomTom);
                    services.AddScoped<IExternalIdentityProvider>(_ => new FakeExternalIdentityProvider("google"));
                    services.AddScoped<IExternalIdentityProvider>(_ => new FakeExternalIdentityProvider("microsoft"));
                });

                // Template Method pattern — allow subclasses to customise the host
                // (e.g. override environment for /e2e/* endpoints)
                ConfigureHost(builder);
            });

        // Warm up the host so the DI container is built before tests run
        _ = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();

        await _dbContainer.DisposeAsync();

        // Clear env vars set in InitializeAsync so they don't bleed into the next test instance.
        // Tests run sequentially (parallelizeAssembly=false), so no race conditions.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);
        Environment.SetEnvironmentVariable("AzureKeyVault__VaultUri", null);
        Environment.SetEnvironmentVariable("KeyVault__Uri", null);
    }

    // ── Protected helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Template Method hook — override to customise the <see cref="WebApplicationFactory{TEntryPoint}"/>
    /// host configuration (e.g. <c>builder.UseEnvironment("Testing")</c> to enable /e2e/* endpoints).
    /// </summary>
    protected virtual void ConfigureHost(IWebHostBuilder builder) { }

    /// <summary>Returns an <see cref="HttpClient"/> configured against the test server.</summary>
    protected HttpClient CreateClient()
    {
        if (_factory is null)
            throw new InvalidOperationException("Factory not yet initialised. Call InitializeAsync first.");

        return _factory.CreateClient();
    }

    /// <summary>
    /// Returns an <see cref="HttpClient"/> that does NOT follow redirects.
    /// Use this when a test needs to inspect the raw 302/301 response and its Location header.
    /// </summary>
    protected HttpClient CreateClientNoRedirect()
    {
        if (_factory is null)
            throw new InvalidOperationException("Factory not yet initialised. Call InitializeAsync first.");

        return _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    /// <summary>Returns the root <see cref="IServiceProvider"/> of the test application host.</summary>
    protected IServiceProvider GetServices()
    {
        if (_factory is null)
            throw new InvalidOperationException("Factory not yet initialised. Call InitializeAsync first.");

        return _factory.Services;
    }

    /// <summary>
    /// Applies all pending EF Core migrations against the test database.
    /// Call this from a test that requires the full schema to be in place.
    /// </summary>
    protected async Task ApplyMigrationsAsync()
    {
        if (_factory is null)
            throw new InvalidOperationException("Factory not yet initialised.");

        using IServiceScope scope = _factory.Services.CreateScope();
        PoTrafficDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<PoTrafficDbContext>();

        await dbContext.Database.MigrateAsync();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Polls the SQL Server engine with a SELECT 1 query until it responds,
    /// retrying up to 60 times with 2-second intervals (120 s total).
    /// </summary>
    private static async Task WaitForSqlReadinessAsync(string connectionString, int maxRetries = 60)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await using SqlConnection conn = new(connectionString);
                await conn.OpenAsync();
                await using SqlCommand cmd = new("SELECT 1", conn);
                await cmd.ExecuteScalarAsync();
                return; // Engine is ready
            }
            catch
            {
                await Task.Delay(2_000);
            }
        }

        throw new TimeoutException(
            $"SQL Server did not become ready within {maxRetries * 2} seconds.");
    }
}
