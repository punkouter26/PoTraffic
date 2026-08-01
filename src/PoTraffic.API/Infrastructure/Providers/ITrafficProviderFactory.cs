using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Infrastructure.Providers;

/// <summary>
/// Factory pattern — resolves the correct ITrafficProvider implementation
/// by RouteProvider enum, decoupling handlers from the DI container internals.
/// DIP: handlers depend on this abstraction; the concrete keyed-DI lookup is in
/// <see cref="KeyedServiceTrafficProviderFactory"/> only.
/// </summary>
public interface ITrafficProviderFactory
{
    ITrafficProvider GetProvider(RouteProvider provider);
}
