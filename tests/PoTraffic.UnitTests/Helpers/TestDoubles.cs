using NSubstitute;
using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.Enums;

namespace PoTraffic.UnitTests.Helpers;

/// <summary>
/// Fixtures every handler test needs. These were previously re-declared verbatim as private
/// statics in ~17 test classes; kept here so a change to how the context or the provider
/// factory is built is one edit rather than seventeen.
/// </summary>
internal static class TestDoubles
{
    /// <summary>A volatile, memory-only <see cref="TableStorageContext"/> (no backing store).</summary>
    public static TableStorageContext CreateDb() => new();

    /// <summary>A factory that hands out <paramref name="provider"/> for every route provider.</summary>
    public static ITrafficProviderFactory ProviderFactory(ITrafficProvider provider)
    {
        var factory = Substitute.For<ITrafficProviderFactory>();
        factory.GetProvider(Arg.Any<RouteProvider>()).Returns(provider);
        return factory;
    }
}
