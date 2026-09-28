using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoTraffic.API.Features.Alerts;
using PoTraffic.API.Features.Config;
using PoTraffic.API.Features.MonitoringWindows;
using PoTraffic.API.Features.Routes;
using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.API.Infrastructure.Scheduling;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.Enums;

namespace PoTraffic.IntegrationTests.Features.Routes;

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
        public int CancelPendingPollJobsForRoute(RouteId routeId) => 0;
        public void ScheduleRecurring(string jobId, Func<Task> job, TimeOnly dailyAtUtc) { }
    }

    private static (PollRouteJob Job, RecordingScheduler Scheduler, ITrafficProvider Traffic, TableStorageContext Db) Build()
    {
        var db = new TableStorageContext();
        var scheduler = new RecordingScheduler();
        // A real poll handler over a substituted provider: "was the route polled" is "was the
        // provider called", which is the quota-spending side effect these tests guard.
        ITrafficProvider traffic = Substitute.For<ITrafficProvider>();
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(db)
            .AddScoped(_ => new ExecutePollCommandHandler(
                db,
                traffic,
                Substitute.For<IWeatherProvider>(),
                new FeatureFlags(UseMockProviders: true, EnableWeather: false),
                new AlertEvaluator(db, Substitute.For<IPushNotifier>(), Substitute.For<IIncidentProvider>(), NullLogger<AlertEvaluator>.Instance),
                NoHolidayCalendar.Instance,
                NullLogger<ExecutePollCommandHandler>.Instance))
            .BuildServiceProvider();
        var job = new PollRouteJob(
            services.GetRequiredService<IServiceScopeFactory>(),
            scheduler,
            NullLogger<PollRouteJob>.Instance);
        return (job, scheduler, traffic, db);
    }

    private static Task AssertNotPolled(ITrafficProvider traffic) =>
        traffic.DidNotReceiveWithAnyArgs().GetTravelTimeAsync(default!, default!, default);

    private static EntityRoute NewRoute(RouteId id, MonitoringStatus status) => new()
    {
        Id = id,
        UserId = UserId.New(),
        OriginAddress = "A",
        OriginCoordinates = "0,0",
        DestinationAddress = "B",
        DestinationCoordinates = "1,1",
        MonitoringStatus = (int)status
    };

    private static MonitoringWindow Window(RouteId routeId, TimeOnly start, TimeOnly end, byte mask = AllDays) => new()
    {
        Id = WindowId.New(),
        RouteId = routeId,
        StartTime = start,
        EndTime = end,
        DaysOfWeekMask = mask,
        IsActive = true
    };

    /// <summary>An always-open window so "now"-dependent tests are deterministic.</summary>
    private static MonitoringWindow AlwaysOpenWindow(RouteId routeId) =>
        Window(routeId, new TimeOnly(0, 0), new TimeOnly(23, 59, 59));

    [Fact]
    public async Task InsideWindow_Polls_SchedulesNextInterval_AndAutoCreatesSession()
    {
        (PollRouteJob job, RecordingScheduler scheduler, ITrafficProvider traffic, TableStorageContext db) = Build();
        RouteId routeId = RouteId.New();
        db.Add(NewRoute(routeId, MonitoringStatus.Active));
        db.Add(AlwaysOpenWindow(routeId));

        await job.Execute(routeId);

        // Inside the window the route must be polled.
        await traffic.ReceivedWithAnyArgs(1).GetTravelTimeAsync(default!, default!, default);
        db.Sessions.Should().ContainSingle(s => s.RouteId == routeId && s.State == (int)SessionState.Active,
            "the daily session is auto-created at window start");
        scheduler.ScheduledDelays.Should().ContainSingle()
            .Which.Should().Be(TimeSpan.FromMinutes(QuotaConstants.PollIntervalMinutes));
        db.Routes.Single(r => r.Id == routeId).JobChainId.Should().Be("job-1");
    }

    [Fact]
    public async Task OutsideWindow_DoesNotPoll_SleepsUntilNextWindowStart()
    {
        (PollRouteJob job, RecordingScheduler scheduler, ITrafficProvider traffic, TableStorageContext db) = Build();
        RouteId routeId = RouteId.New();
        db.Add(NewRoute(routeId, MonitoringStatus.Active));

        // A one-minute window that is never "now": start = now + 2h (wrapping within the day is fine —
        // NextWindowStart then lands tomorrow, still a positive delay).
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeOnly start = TimeOnly.FromDateTime(now.UtcDateTime.AddHours(2));
        db.Add(Window(routeId, start, start.AddMinutes(1)));

        await job.Execute(routeId);

        // No provider quota may be spent outside the monitoring window.
        await AssertNotPolled(traffic);
        scheduler.ScheduledDelays.Should().ContainSingle();
        scheduler.ScheduledDelays[0].Should().BeGreaterThan(TimeSpan.FromMinutes(QuotaConstants.PollIntervalMinutes),
            "the chain sleeps until the window opens instead of ticking every interval");
        scheduler.ScheduledDelays[0].Should().BeLessThanOrEqualTo(TimeSpan.FromDays(7).Add(TimeSpan.FromHours(2)));
    }

    [Fact]
    public async Task NoActiveWindow_StopsChain_AndClearsJobChainId()
    {
        (PollRouteJob job, RecordingScheduler scheduler, ITrafficProvider traffic, TableStorageContext db) = Build();
        RouteId routeId = RouteId.New();
        EntityRoute route = NewRoute(routeId, MonitoringStatus.Active);
        route.JobChainId = "stale";
        db.Add(route);

        await job.Execute(routeId);

        await AssertNotPolled(traffic);
        scheduler.ScheduledDelays.Should().BeEmpty("a route without a window has nothing to sample");
        db.Routes.Single(r => r.Id == routeId).JobChainId.Should().BeNull();
    }

    [Fact]
    public async Task DeletedRoute_StopsChain()
    {
        (PollRouteJob job, RecordingScheduler scheduler, ITrafficProvider traffic, TableStorageContext db) = Build();
        RouteId routeId = RouteId.New();
        db.Add(NewRoute(routeId, MonitoringStatus.Deleted));
        db.Add(AlwaysOpenWindow(routeId));

        await job.Execute(routeId);

        await AssertNotPolled(traffic);
        scheduler.ScheduledDelays.Should().BeEmpty("a soft-deleted route must not consume provider quota");
    }

    [Fact]
    public async Task MissingRoute_StopsChain()
    {
        (PollRouteJob job, RecordingScheduler scheduler, _, _) = Build();

        await job.Execute(RouteId.New());

        scheduler.ScheduledDelays.Should().BeEmpty("a hard-deleted route must not keep polling");
    }

    [Fact]
    public async Task QuotaExhausted_DoesNotPoll_SleepsUntilNextWindowStart()
    {
        (PollRouteJob job, RecordingScheduler scheduler, ITrafficProvider traffic, TableStorageContext db) = Build();
        RouteId routeId = RouteId.New();
        EntityRoute route = NewRoute(routeId, MonitoringStatus.Active);
        db.Add(route);
        db.Add(AlwaysOpenWindow(routeId));

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        for (int i = 0; i < QuotaConstants.DefaultDailyQuota; i++)
        {
            RouteId otherRouteId = RouteId.New();
            db.Add(new EntityRoute
            {
                Id = otherRouteId,
                UserId = route.UserId,
                OriginAddress = "A",
                OriginCoordinates = "0,0",
                DestinationAddress = "B",
                DestinationCoordinates = "1,1"
            });
            db.Add(new MonitoringSession { Id = SessionId.New(), RouteId = otherRouteId, SessionDate = today, State = (int)SessionState.Completed });
        }

        await job.Execute(routeId);

        // The per-user daily session quota must be honoured.
        await AssertNotPolled(traffic);
        scheduler.ScheduledDelays.Should().ContainSingle("the chain resumes at the next window start");
    }
}
