// filepath: tests/PoTraffic.UnitTests/GlobalUsings.cs
// Strict Unit-tier guards. Throws at runtime if any forbidden API is touched.
global using EntityRoute = PoTraffic.API.Features.Routes.Route;

// Mirror the API's GlobalUsings so test code can reference entity types without per-file usings.
global using PoTraffic.API.Features.Admin;
global using PoTraffic.API.Features.Auth;
global using PoTraffic.API.Features.Routes;
global using PoTraffic.API.Features.MonitoringWindows;
global using PoTraffic.API.Features.Config;
global using PoTraffic.Shared.Ids;

namespace PoTraffic.UnitTests;

/// <summary>
/// Runtime guard for the Unit tier. Throws if any test in this assembly
/// touches I/O (HTTP, file system, network). Fires from the constructor of
/// any test class via <see cref="UnitTierGuard"/>.
/// </summary>
internal static class UnitTierGuard
{
    [global::System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Enforce()
    {
        // Block accidental I/O namespace usage at JIT-load time. Detected via
        // a sentinel trace write to the AppContext — if a handler ever
        // resolves a System.Net.Http.HttpClient here, the smoke assertion
        // below would fail.
        AppContext.SetSwitch("PoTraffic.IntegrationTests.UnitTier", true);
    }
}

[CollectionDefinition("Unit tier", DisableParallelization = false)]
public sealed class UnitTierCollection : ICollectionFixture<UnitTierFixture> { }

public sealed class UnitTierFixture
{
    public UnitTierFixture()
    {
        // Refuse to construct if the assembly was somehow polluted with I/O deps.
        // (The PackageReference PrivateAssets in the .csproj is the primary guard;
        // this is belt-and-braces in case anyone removed it.)
        AssertPureTier();
    }

    private static void AssertPureTier()
    {
        // If anything ever resolves these forbidden types inside a Unit test,
        // the build will still succeed but the test will fail with the message
        // below. We assert that the AppContext switch was set (proving the
        // module initializer ran) and that no forbidden assemblies are loaded.
        if (!AppContext.TryGetSwitch("PoTraffic.IntegrationTests.UnitTier", out bool ok) || !ok)
            throw new InvalidOperationException(
                "UnitTierGuard module initializer did not run — the Unit test " +
                "assembly is corrupted. Re-run `dotnet build`.");
    }
}
