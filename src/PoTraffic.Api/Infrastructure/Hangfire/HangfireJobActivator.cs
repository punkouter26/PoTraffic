using Hangfire;

namespace PoTraffic.Api.Infrastructure.Hangfire;

/// <summary>
/// Adapter pattern — bridges Hangfire job activation to ASP.NET Core DI scope lifecycle.
/// Each job is resolved within its own <see cref="IServiceScope"/>, which is disposed
/// once the job method returns.
/// </summary>
public sealed class HangfireJobActivator : JobActivator
{
    private readonly IServiceScopeFactory _scopeFactory;

    public HangfireJobActivator(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public override object ActivateJob(Type jobType)
    {
        IServiceScope scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService(jobType);
    }
}
