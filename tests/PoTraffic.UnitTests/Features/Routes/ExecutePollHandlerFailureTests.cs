using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Shared.Enums;

namespace PoTraffic.UnitTests.Features.Routes;

/// <summary>
/// Failure-path tests for <see cref="ExecutePollCommandHandler"/>.
/// FR-005: provider exceptions must not propagate out of the handler;
/// they are caught, logged as warnings, and the poll is silently skipped.
/// </summary>
public sealed class ExecutePollHandlerFailureTests
{
    private static PoTrafficDbContext CreateDb(string name)
    {
        DbContextOptions<PoTrafficDbContext> opts = new DbContextOptionsBuilder<PoTrafficDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new PoTrafficDbContext(opts);
    }

    private static ITrafficProviderFactory BuildProviderFactory(ITrafficProvider provider)
    {
        var factory = Substitute.For<ITrafficProviderFactory>();
        factory.GetProvider(Arg.Any<RouteProvider>()).Returns(provider);
        
        return factory;
    }

    private static async Task<(PoTrafficDbContext Db, Guid RouteId, Guid SessionId)> SeedAsync(string dbName)
    {
        PoTrafficDbContext db = CreateDb(dbName);
        Guid routeId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();

        db.Routes.Add(new Route
        {
            Id = routeId,
            UserId = Guid.NewGuid(),
            OriginAddress = "A",
            OriginCoordinates = "1.0,1.0",
            DestinationAddress = "B",
            DestinationCoordinates = "2.0,2.0",
            Provider = (int)RouteProvider.GoogleMaps,
            MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.MonitoringSessions.Add(new MonitoringSession
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
        string dbName = Guid.NewGuid().ToString();
        (PoTrafficDbContext db, Guid routeId, Guid sessionId) = await SeedAsync(dbName);

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        mockProvider
            .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        ILogger<ExecutePollCommandHandler> logger = Substitute.For<ILogger<ExecutePollCommandHandler>>();
        ITrafficProviderFactory providerFactory = BuildProviderFactory(mockProvider);
        var handler = new ExecutePollCommandHandler(db, providerFactory, logger);

        // Act
        bool result = await handler.Handle(new ExecutePollCommand(routeId), CancellationToken.None);

        // Assert — FR-005: returns false, no exception propagated
        result.Should().BeFalse("provider errors must not propagate to Hangfire caller (FR-005)");

        int pollCount = await db.PollRecords.CountAsync(p => p.RouteId == routeId);
        pollCount.Should().Be(0, "no PollRecord should be inserted when provider throws (FR-005)");
    }

    [Fact]
    public async Task WhenProviderThrowsHttpRequestException_PollCountUnchanged()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        (PoTrafficDbContext db, Guid routeId, Guid sessionId) = await SeedAsync(dbName);

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        mockProvider
            .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Timeout"));

        ITrafficProviderFactory providerFactory = BuildProviderFactory(mockProvider);
        var handler = new ExecutePollCommandHandler(
            db, providerFactory, Substitute.For<ILogger<ExecutePollCommandHandler>>());

        // Act
        await handler.Handle(new ExecutePollCommand(routeId), CancellationToken.None);

        // Assert — session's PollCount must not be incremented on failure (FR-005)
        MonitoringSession? session = await db.MonitoringSessions.FindAsync(sessionId);
        session.Should().NotBeNull();
        session!.PollCount.Should().Be(3, "PollCount must remain unchanged when provider throws (FR-005)");
    }

    [Fact]
    public async Task WhenProviderThrowsHttpRequestException_WarningIsLogged()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        (PoTrafficDbContext db, Guid routeId, _) = await SeedAsync(dbName);

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        mockProvider
            .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("DNS failure"));

        ILogger<ExecutePollCommandHandler> logger = Substitute.For<ILogger<ExecutePollCommandHandler>>();
        ITrafficProviderFactory providerFactory = BuildProviderFactory(mockProvider);
        var handler = new ExecutePollCommandHandler(db, providerFactory, logger);

        // Act
        await handler.Handle(new ExecutePollCommand(routeId), CancellationToken.None);

        // Assert — a warning log must be emitted (FR-005)
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task WhenProviderThrowsHttpRequestException_NoExceptionPropagates()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        (PoTrafficDbContext db, Guid routeId, _) = await SeedAsync(dbName);

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        mockProvider
            .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network unreachable"));

        ITrafficProviderFactory providerFactory = BuildProviderFactory(mockProvider);
        var handler = new ExecutePollCommandHandler(
            db, providerFactory, Substitute.For<ILogger<ExecutePollCommandHandler>>());

        // Act — must not throw; Hangfire cannot handle uncaught exceptions in this design
        Func<Task> act = async () =>
            await handler.Handle(new ExecutePollCommand(routeId), CancellationToken.None);

        await act.Should().NotThrowAsync(
            "provider errors must be swallowed inside the handler so Hangfire does not retry the job (FR-005)");
    }
}
