using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace PoTraffic.IntegrationTests.Infrastructure;

/// <summary>
/// Spins up Azurite Table Storage inside Testcontainers, owned for the
/// lifetime of the test run. Lifecycle-managed via <see cref="IAsyncDisposable"/>
/// — the container is explicitly stopped and removed the moment the run
/// ends (success OR failure), so no orphan containers linger on the host.
/// </summary>
internal sealed class AzuriteTestContainer : IAsyncDisposable
{
    private const string AccountName = "devstoreaccount1";
    private const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static AzuriteTestContainer? _instance;

    public IContainer Container { get; }
    public string ConnectionString { get; }

    private AzuriteTestContainer(IContainer container, string connectionString)
    {
        Container = container;
        ConnectionString = connectionString;
    }

    public static async Task<AzuriteTestContainer> GetInstanceAsync()
    {
        if (_instance is not null)
            return _instance;

        await Gate.WaitAsync();
        try
        {
            if (_instance is not null)
                return _instance;

            var container = new ContainerBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
                .WithName($"potraffic-azurite-test-{Guid.NewGuid():N}")
                .WithPortBinding(10002, true)
                .WithCommand("azurite-table", "--tableHost", "0.0.0.0", "--loose", "--location", "/data")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(10002))
                // Auto-remove on host shutdown so the container cannot become an orphan
                // even if DisposeAsync is not awaited (e.g. SIGKILL on the test runner).
                .WithCleanUp(true)
                .Build();

            await container.StartAsync();

            ushort tablePort = container.GetMappedPublicPort(10002);
            string connectionString =
                $"DefaultEndpointsProtocol=http;AccountName={AccountName};AccountKey={AccountKey};" +
                $"TableEndpoint=http://127.0.0.1:{tablePort}/{AccountName};";

            _instance = new AzuriteTestContainer(container, connectionString);
            return _instance;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Backwards-compat shim — existing tests that used the old static API
    /// (<c>GetConnectionStringAsync()</c>) still work without modification.
    /// </summary>
    public static async Task<string> GetConnectionStringAsync()
    {
        var instance = await GetInstanceAsync();
        return instance.ConnectionString;
    }

    /// <summary>
    /// Explicit teardown — called by <c>SCRIPTS/run-tests.ps1</c> after the test
    /// run finishes (regardless of pass/fail). Removes the container from the
    /// host so it cannot leak.
    /// </summary>
    public static async ValueTask DisposeInstanceAsync()
    {
        if (_instance is null) return;
        var local = _instance;
        _instance = null;
        await local.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Container.State == TestcontainersStates.Running)
                await Container.StopAsync();
        }
        catch
        {
            // Best-effort — Testcontainers resource reaper handles leftovers.
        }
        finally
        {
            try { await Container.DisposeAsync(); } catch { /* swallow */ }
        }
    }
}
