using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Api.Infrastructure.Dispatch;
using PoTraffic.Api.Infrastructure.Scheduling;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Shared.Enums;

namespace PoTraffic.UnitTests.Features.Routes;

/// <summary>
/// The self-scheduling poll chain must stop when its route disappears or is
/// soft-deleted mid-poll — otherwise deleted routes keep consuming provider quota.
/// </summary>
public sealed class PollRouteJobTests
{
    private sealed class RecordingScheduler : IJobScheduler
    {
        public int ScheduleCalls { get; private set; }
        public string Enqueue(Expression<Func<Task>> job) => "job-0";
        public string Schedule(Expression<Func<Task>> job, TimeSpan delay) { ScheduleCalls++; return $"job-{ScheduleCalls}"; }
        public void Cancel(string jobId) { }
        public void ScheduleRecurring(string jobId, Func<Task> job, string cronExpression) { }
        public void CancelRecurring(string jobId) { }
    }

    private sealed class NoOpSender : ISender
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
            => Task.FromResult<TResponse>(default!);
    }

    private static (PollRouteJob Job, RecordingScheduler Scheduler, TableStorageContext Db) Build()
    {
        var db = new TableStorageContext();
        var scheduler = new RecordingScheduler();
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(db)
            .AddScoped<ISender, NoOpSender>()
            .BuildServiceProvider();
        var job = new PollRouteJob(
            services.GetRequiredService<IServiceScopeFactory>(),
            scheduler,
            NullLogger<PollRouteJob>.Instance);
        return (job, scheduler, db);
    }

    private static EntityRoute NewRoute(Guid id, MonitoringStatus status) => new()
    {
        Id = id,
        UserId = Guid.NewGuid(),
        OriginAddress = "A",
        OriginCoordinates = "0,0",
        DestinationAddress = "B",
        DestinationCoordinates = "1,1",
        MonitoringStatus = (int)status
    };

    [Fact]
    public async Task ActiveRoute_SchedulesSuccessor_AndTracksJobChainId()
    {
        (PollRouteJob job, RecordingScheduler scheduler, TableStorageContext db) = Build();
        Guid routeId = Guid.NewGuid();
        db.Add(NewRoute(routeId, MonitoringStatus.Active));

        await job.Execute(routeId);

        scheduler.ScheduleCalls.Should().Be(1, "an active route keeps its poll chain alive");
        db.Routes.Single(r => r.Id == routeId).JobChainId.Should().Be("job-1");
    }

    [Fact]
    public async Task DeletedRoute_StopsChain()
    {
        (PollRouteJob job, RecordingScheduler scheduler, TableStorageContext db) = Build();
        Guid routeId = Guid.NewGuid();
        db.Add(NewRoute(routeId, MonitoringStatus.Deleted));

        await job.Execute(routeId);

        scheduler.ScheduleCalls.Should().Be(0, "a soft-deleted route must not consume provider quota");
    }

    [Fact]
    public async Task MissingRoute_StopsChain()
    {
        (PollRouteJob job, RecordingScheduler scheduler, _) = Build();

        await job.Execute(Guid.NewGuid());

        scheduler.ScheduleCalls.Should().Be(0, "a hard-deleted route must not keep polling");
    }
}
