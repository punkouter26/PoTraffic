using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PoTraffic.Api.Features.MonitoringWindows.Entities;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Api.Features.Routes.Entities;
using PoTraffic.Api.Infrastructure.Dispatch;
using PoTraffic.Api.Infrastructure.Scheduling;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Tests.Features.Routes;

/// <summary>
/// The self-scheduling poll chain must sample only inside the route's active
/// monitoring window (UTC), sleep until the next window start otherwise, and
/// stop entirely when its route disappears or is soft-deleted.
/// </summary>
public sealed class PollRouteJobTests
{
    private const byte AllDays = 0x7F;

    private sealed class RecordingScheduler : IJobScheduler
    {
        public List<TimeSpan> ScheduledDelays { get; } = [];
        public string Enqueue(Expression<Func<Task>> job) => "job-0";
        public string Schedule(Expression<Func<Task>> job, TimeSpan delay)
        {
            ScheduledDelays.Add(delay);
            return $"job-{ScheduledDelays.Count}";
        }
        public void Cancel(string jobId) { }
        public int CancelPendingPollJobsForRoute(Guid routeId) => 0;
        public void ScheduleRecurring(string jobId, Func<Task> job, string cronExpression) { }
        public void CancelRecurring(string jobId) { }
    }

    private sealed class RecordingSender : ISender
    {
        public List<object> Sent { get; } = [];
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
        {
            Sent.Add(request);
            return Task.FromResult<TResponse>(default!);
        }
    }

    private static (PollRouteJob Job, RecordingScheduler Scheduler, RecordingSender Sender, TableStorageContext Db) Build()
    {
        var db = new TableStorageContext();
        var scheduler = new RecordingScheduler();
        var sender = new RecordingSender();
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(db)
            .AddScoped<ISender>(_ => sender)
            .BuildServiceProvider();
        var job = new PollRouteJob(
            services.GetRequiredService<IServiceScopeFactory>(),
            scheduler,
            NullLogger<PollRouteJob>.Instance);
        return (job, scheduler, sender, db);
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

    private static MonitoringWindow Window(Guid routeId, TimeOnly start, TimeOnly end, byte mask = AllDays) => new()
    {
        Id = Guid.NewGuid(),
        RouteId = routeId,
        StartTime = start,
        EndTime = end,
        DaysOfWeekMask = mask,
        IsActive = true
    };

    /// <summary>An always-open window so "now"-dependent tests are deterministic.</summary>
    private static MonitoringWindow AlwaysOpenWindow(Guid routeId) =>
        Window(routeId, new TimeOnly(0, 0), new TimeOnly(23, 59, 59));

    [Fact]
    public async Task InsideWindow_Polls_SchedulesNextInterval_AndAutoCreatesSession()
    {
        (PollRouteJob job, RecordingScheduler scheduler, RecordingSender sender, TableStorageContext db) = Build();
        Guid routeId = Guid.NewGuid();
        db.Add(NewRoute(routeId, MonitoringStatus.Active));
        db.Add(AlwaysOpenWindow(routeId));

        await job.Execute(routeId);

        sender.Sent.Should().ContainSingle(r => r is ExecutePollCommand, "inside the window the route must be polled");
        db.Sessions.Should().ContainSingle(s => s.RouteId == routeId && s.State == (int)SessionState.Active,
            "the daily session is auto-created at window start");
        scheduler.ScheduledDelays.Should().ContainSingle()
            .Which.Should().Be(TimeSpan.FromMinutes(QuotaConstants.PollIntervalMinutes));
        db.Routes.Single(r => r.Id == routeId).JobChainId.Should().Be("job-1");
    }

    [Fact]
    public async Task OutsideWindow_DoesNotPoll_SleepsUntilNextWindowStart()
    {
        (PollRouteJob job, RecordingScheduler scheduler, RecordingSender sender, TableStorageContext db) = Build();
        Guid routeId = Guid.NewGuid();
        db.Add(NewRoute(routeId, MonitoringStatus.Active));

        // A one-minute window that is never "now": start = now + 2h (wrapping within the day is fine —
        // NextWindowStart then lands tomorrow, still a positive delay).
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeOnly start = TimeOnly.FromDateTime(now.UtcDateTime.AddHours(2));
        db.Add(Window(routeId, start, start.AddMinutes(1)));

        await job.Execute(routeId);

        sender.Sent.Should().BeEmpty("no provider quota may be spent outside the monitoring window");
        scheduler.ScheduledDelays.Should().ContainSingle();
        scheduler.ScheduledDelays[0].Should().BeGreaterThan(TimeSpan.FromMinutes(QuotaConstants.PollIntervalMinutes),
            "the chain sleeps until the window opens instead of ticking every interval");
        scheduler.ScheduledDelays[0].Should().BeLessThanOrEqualTo(TimeSpan.FromDays(7).Add(TimeSpan.FromHours(2)));
    }

    [Fact]
    public async Task NoActiveWindow_StopsChain_AndClearsJobChainId()
    {
        (PollRouteJob job, RecordingScheduler scheduler, RecordingSender sender, TableStorageContext db) = Build();
        Guid routeId = Guid.NewGuid();
        EntityRoute route = NewRoute(routeId, MonitoringStatus.Active);
        route.JobChainId = "stale";
        db.Add(route);

        await job.Execute(routeId);

        sender.Sent.Should().BeEmpty();
        scheduler.ScheduledDelays.Should().BeEmpty("a route without a window has nothing to sample");
        db.Routes.Single(r => r.Id == routeId).JobChainId.Should().BeNull();
    }

    [Fact]
    public async Task DeletedRoute_StopsChain()
    {
        (PollRouteJob job, RecordingScheduler scheduler, RecordingSender sender, TableStorageContext db) = Build();
        Guid routeId = Guid.NewGuid();
        db.Add(NewRoute(routeId, MonitoringStatus.Deleted));
        db.Add(AlwaysOpenWindow(routeId));

        await job.Execute(routeId);

        sender.Sent.Should().BeEmpty();
        scheduler.ScheduledDelays.Should().BeEmpty("a soft-deleted route must not consume provider quota");
    }

    [Fact]
    public async Task MissingRoute_StopsChain()
    {
        (PollRouteJob job, RecordingScheduler scheduler, _, _) = Build();

        await job.Execute(Guid.NewGuid());

        scheduler.ScheduledDelays.Should().BeEmpty("a hard-deleted route must not keep polling");
    }

    [Fact]
    public async Task QuotaExhausted_DoesNotPoll_SleepsUntilNextWindowStart()
    {
        (PollRouteJob job, RecordingScheduler scheduler, RecordingSender sender, TableStorageContext db) = Build();
        Guid routeId = Guid.NewGuid();
        EntityRoute route = NewRoute(routeId, MonitoringStatus.Active);
        db.Add(route);
        db.Add(AlwaysOpenWindow(routeId));

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        for (int i = 0; i < QuotaConstants.DefaultDailyQuota; i++)
        {
            Guid otherRouteId = Guid.NewGuid();
            db.Add(new EntityRoute
            {
                Id = otherRouteId,
                UserId = route.UserId,
                OriginAddress = "A",
                OriginCoordinates = "0,0",
                DestinationAddress = "B",
                DestinationCoordinates = "1,1"
            });
            db.Add(new MonitoringSession { Id = Guid.NewGuid(), RouteId = otherRouteId, SessionDate = today, State = (int)SessionState.Completed });
        }

        await job.Execute(routeId);

        sender.Sent.Should().BeEmpty("the per-user daily session quota must be honoured");
        scheduler.ScheduledDelays.Should().ContainSingle("the chain resumes at the next window start");
    }

    [Fact]
    public void NextWindowStart_SkipsDisabledDays()
    {
        // Monday-only window at 08:00 UTC; from a Tuesday the next start is the following Monday.
        var window = Window(Guid.NewGuid(), new TimeOnly(8, 0), new TimeOnly(9, 0), mask: 0b0000001);
        DateTimeOffset tuesday = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero); // Tuesday

        DateTimeOffset? next = PollRouteJob.NextWindowStart(window, tuesday);

        next.Should().Be(new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero)); // next Monday 08:00
    }

    [Fact]
    public void NextWindowStart_NoDaysEnabled_ReturnsNull()
    {
        var window = Window(Guid.NewGuid(), new TimeOnly(8, 0), new TimeOnly(9, 0), mask: 0);
        PollRouteJob.NextWindowStart(window, DateTimeOffset.UtcNow).Should().BeNull();
    }

    [Fact]
    public void IsWithinWindow_RespectsDayMaskAndTimeRange()
    {
        // Mon–Fri 07:00–09:00 (mask 0x1F, bit0=Monday)
        var window = Window(Guid.NewGuid(), new TimeOnly(7, 0), new TimeOnly(9, 0), mask: 0x1F);

        PollRouteJob.IsWithinWindow(window, new DateTimeOffset(2026, 7, 6, 8, 0, 0, TimeSpan.Zero))
            .Should().BeTrue("Monday 08:00 is inside Mon–Fri 07:00–09:00");
        PollRouteJob.IsWithinWindow(window, new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.Zero))
            .Should().BeFalse("the end time is exclusive");
        PollRouteJob.IsWithinWindow(window, new DateTimeOffset(2026, 7, 5, 8, 0, 0, TimeSpan.Zero))
            .Should().BeFalse("Sunday is not in the Mon–Fri mask");
    }
}
