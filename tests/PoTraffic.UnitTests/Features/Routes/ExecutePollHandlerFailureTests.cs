using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PoTraffic.API.Features.Routes;
using PoTraffic.API.Infrastructure.Storage;

using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.Shared.Enums;
using PoTraffic.UnitTests.Helpers;

namespace PoTraffic.UnitTests.Features.Routes;

/// <summary>
/// Failure-path tests for <see cref="ExecutePollCommandHandler"/>.
/// FR-005: provider exceptions must not propagate out of the handler;
/// they are caught, logged as warnings, and the poll is silently skipped.
/// </summary>
// Consolidated: this scenario (provider throws HttpRequestException mid-poll) was arranged
// and executed four separate times, each asserting one facet — no exception escapes, the
// handler returns false, no PollRecord lands, PollCount is untouched, a warning is logged.
// One arrange-act with the full set of assertions proves the same contract at a quarter of
// the setup, and a failure now names every consequence at once instead of one of five.
public sealed class ExecutePollHandlerFailureTests
{


    private static async Task<(TableStorageContext Db, RouteId RouteId, SessionId SessionId)> SeedAsync()
    {
        TableStorageContext db = TestDoubles.CreateDb();
        RouteId routeId = RouteId.New();
        SessionId sessionId = SessionId.New();

        db.Add(new Route
        {
            Id = routeId,
            UserId = UserId.New(),
            OriginAddress = "A",
            OriginCoordinates = "1.0,1.0",
            DestinationAddress = "B",
            DestinationCoordinates = "2.0,2.0",
            Provider = (int)RouteProvider.GoogleMaps,
            MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.Add(new MonitoringSession
        {
            Id = sessionId,
            RouteId = routeId,
            SessionDate = DateOnly.FromDateTime(DateTime.UtcNow),
            State = (int)SessionState.Active,
            PollCount = 3
        });

        await db.SaveChangesAsync();
        return (db, routeId, sessionId);
    }

    [Fact]
    public async Task WhenProviderThrowsHttpRequestException_ReturnsFalse_NoPollRecordInserted()
    {
        // Arrange
        (TableStorageContext db, RouteId routeId, SessionId sessionId) = await SeedAsync();

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        mockProvider
            .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        ILogger<ExecutePollCommandHandler> logger = Substitute.For<ILogger<ExecutePollCommandHandler>>();
        ITrafficProviderFactory providerFactory = TestDoubles.ProviderFactory(mockProvider);
        var handler = PoTraffic.UnitTests.Helpers.PollHandlerTestHelper.Create(db, providerFactory, logger);

        // Act
        bool result = await handler.Handle(new ExecutePollCommand(routeId), CancellationToken.None);

        // Assert — FR-005, every consequence of a thrown provider call in one place.
        // Reaching this line at all is the "no exception propagates" assertion.
        result.Should().BeFalse("provider errors must not propagate to caller (FR-005)");

        int pollCount = db.PollRecords.Count(p => p.RouteId == routeId);
        pollCount.Should().Be(0, "no PollRecord should be inserted when provider throws (FR-005)");

        // The seeded session already carries 3 polls, so the contract is "unchanged", not
        // "zero" — a failed provider call must neither add to nor reset the running count.
        MonitoringSession session = db.MonitoringSessions.Single(s => s.Id == sessionId);
        session.PollCount.Should().Be(3, "a failed provider call must not count against the session");

        logger.ReceivedWithAnyArgs().Log(
            LogLevel.Warning, default, default!, default, default!);
    }

}
