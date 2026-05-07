using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Infrastructure.Providers;

/// <summary>
/// Concrete factory that resolves ITrafficProvider via ASP.NET Core keyed DI.
/// This is the only place in the codebase that casts IServiceProvider to
/// IKeyedServiceProvider — handlers stay clean and testable.
/// </summary>
public sealed class KeyedServiceTrafficProviderFactory : ITrafficProviderFactory
{
    private readonly IKeyedServiceProvider _sp;

    public KeyedServiceTrafficProviderFactory(IServiceProvider sp)
    {
        // IKeyedServiceProvider is implemented by ASP.NET Core's built-in container.
        _sp = (IKeyedServiceProvider)sp;
    }

    public ITrafficProvider GetProvider(RouteProvider provider) =>
        _sp.GetRequiredKeyedService<ITrafficProvider>(provider);
}
