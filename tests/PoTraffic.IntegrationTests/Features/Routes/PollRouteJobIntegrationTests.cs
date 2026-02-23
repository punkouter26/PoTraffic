namespace PoTraffic.IntegrationTests.Features.Routes;

/// <summary>
/// Integration tests for <see cref="PoTraffic.Api.Features.Routes.PollRouteJob"/> execution.
///
/// These tests require a running Docker daemon and the azure-sql-edge container image.
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
public sealed class PollRouteJobIntegrationTests : BaseIntegrationTest
{
    [Fact(Skip = "Requires Docker — run manually with TESTCONTAINERS_STARTUP_TIMEOUT=120")]
    public async Task PollRouteJob_InsertsPollRecordAndIncrementsPollCount()
    {
        // Arrange
        // TODO: Seed a route with an active MonitoringSession for today,
        // directly invoke PollRouteJob.Execute(routeId) via the DI container,
        // then assert:
        //   1. A new PollRecord row exists with SessionId set
        //   2. MonitoringSession.PollCount has incremented by 1
        //
        // Example (skeleton):
        // using var scope = GetScope();
        // var job = scope.ServiceProvider.GetRequiredService<PollRouteJob>();
        // await job.Execute(routeId);
        //
        // var db = scope.ServiceProvider.GetRequiredService<PoTrafficDbContext>();
        // var record = await db.PollRecords.FirstAsync(p => p.RouteId == routeId);
        // record.SessionId.Should().Be(sessionId);
        //
        // var session = await db.MonitoringSessions.FindAsync(sessionId);
        // session!.PollCount.Should().Be(1);

        await Task.CompletedTask; // placeholder
    }
}
