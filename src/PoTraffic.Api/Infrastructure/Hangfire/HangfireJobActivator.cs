using Hangfire;

namespace PoTraffic.Api.Infrastructure.Hangfire;

/// <summary>
/// Adapter pattern — bridges Hangfire job activation to ASP.NET Core DI scope lifecycle.
/// Each job runs inside a <see cref="ServiceScopeJobActivatorScope"/> that owns and
/// disposes the <see cref="IServiceScope"/> when Hangfire calls <see cref="JobActivatorScope.DisposeScope"/>.
/// </summary>
public sealed class HangfireJobActivator : JobActivator
{
    private readonly IServiceScopeFactory _scopeFactory;

    public HangfireJobActivator(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public override JobActivatorScope BeginScope(JobActivatorContext context)
        => new ServiceScopeJobActivatorScope(_scopeFactory.CreateScope());

    private sealed class ServiceScopeJobActivatorScope : JobActivatorScope
    {
        private readonly IServiceScope _scope;

        public ServiceScopeJobActivatorScope(IServiceScope scope) => _scope = scope;

        public override object Resolve(Type type) => _scope.ServiceProvider.GetRequiredService(type);

        public override void DisposeScope() => _scope.Dispose();
    }
}
