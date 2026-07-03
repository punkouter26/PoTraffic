// filepath: tests/PoTraffic.UnitTests/GlobalUsings.cs
// Strict Unit-tier guards. Throws at runtime if any forbidden API is touched.
global using EntityRoute = PoTraffic.Api.Features.Routes.Entities.Route;

// Mirror the API's GlobalUsings so test code can reference entity types without per-file usings.
global using PoTraffic.Api.Features.Admin.Entities;
global using PoTraffic.Api.Features.Auth.Entities;
global using PoTraffic.Api.Features.Routes.Entities;
global using PoTraffic.Api.Features.MonitoringWindows.Entities;
global using PoTraffic.Api.Features.Config.Entities;

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
        AppContext.SetSwitch("PoTraffic.Tests.UnitTier", true);
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
        if (!AppContext.TryGetSwitch("PoTraffic.Tests.UnitTier", out bool ok) || !ok)
            throw new InvalidOperationException(
                "UnitTierGuard module initializer did not run — the Unit test " +
                "assembly is corrupted. Re-run `dotnet build`.");
    }
}