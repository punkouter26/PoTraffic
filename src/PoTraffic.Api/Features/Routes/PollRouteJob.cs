using Hangfire;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Shared.Constants;

namespace PoTraffic.Api.Features.Routes;

// Chain of Responsibility pattern — each job enqueues its own successor to maintain the polling chain
public sealed class PollRouteJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBackgroundJobClient _jobClient;
    private readonly ILogger<PollRouteJob> _logger;

    public PollRouteJob(
        IServiceScopeFactory scopeFactory,
        IBackgroundJobClient jobClient,
        ILogger<PollRouteJob> logger)
    {
        _scopeFactory = scopeFactory;
        _jobClient = jobClient;
        _logger = logger;
    }

    public async Task Execute(Guid routeId)
    {
        _logger.LogInformation("PollRouteJob executing for route {RouteId}", routeId);

        // Use a fresh DI scope for the handler (avoids DbContext state pollution across polls)
        using (IServiceScope scope = _scopeFactory.CreateScope())
        {
            ISender sender = scope.ServiceProvider.GetRequiredService<ISender>();

            try
            {
                await sender.Send(new ExecutePollCommand(routeId));
            }
            catch (Exception ex)
            {
                // Log but do not rethrow — Hangfire must not retry on handler errors
                _logger.LogError(ex, "PollRouteJob: Unhandled error for route {RouteId}", routeId);
            }
        }

        // Schedule next execution — Chain of Responsibility enqueues its own successor
        string nextJobId = _jobClient.Schedule<PollRouteJob>(
            job => job.Execute(routeId),
            TimeSpan.FromMinutes(QuotaConstants.PollIntervalMinutes));

        _logger.LogInformation(
            "PollRouteJob: Next poll for route {RouteId} scheduled as job {JobId}", routeId, nextJobId);

        // Update route HangfireJobChainId with successor job ID
        using IServiceScope updateScope = _scopeFactory.CreateScope();
        PoTrafficDbContext db = updateScope.ServiceProvider.GetRequiredService<PoTrafficDbContext>();
        EntityRoute? route = await db.Routes.FindAsync(routeId);
        if (route is not null)
        {
            route.HangfireJobChainId = nextJobId;
            await db.SaveChangesAsync();
        }
    }
}
