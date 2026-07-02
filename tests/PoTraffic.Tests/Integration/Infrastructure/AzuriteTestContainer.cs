using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace PoTraffic.Tests.Infrastructure;

internal static class AzuriteTestContainer
{
    private const string AccountName = "devstoreaccount1";
    private const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IContainer? _container;
    private static string? _connectionString;
    private static bool _disposeRegistered;

    public static async Task<string> GetConnectionStringAsync()
    {
        if (_connectionString is not null)
            return _connectionString;

        await Gate.WaitAsync();
        try
        {
            if (_connectionString is not null)
                return _connectionString;

            _container = new ContainerBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
                .WithName($"potraffic-azurite-test-{Guid.NewGuid():N}")
                .WithPortBinding(10002, true)
                .WithCommand("azurite-table", "--tableHost", "0.0.0.0", "--loose", "--location", "/data")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(10002))
                .Build();

            await _container.StartAsync();

            ushort tablePort = _container.GetMappedPublicPort(10002);
            _connectionString =
                $"DefaultEndpointsProtocol=http;AccountName={AccountName};AccountKey={AccountKey};" +
                $"TableEndpoint=http://127.0.0.1:{tablePort}/{AccountName};";

            RegisterProcessCleanup();
            return _connectionString;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void RegisterProcessCleanup()
    {
        if (_disposeRegistered)
            return;

        _disposeRegistered = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                _container?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Best-effort cleanup. Testcontainers' resource reaper handles leftovers.
            }
        };
    }
}
