using FluentValidation;
using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Admin;

/// <summary>
/// Geocodes the supplied addresses, persists a TripleTestSession with 3 shot stubs,
/// then schedules 3 Hangfire jobs at t=0s, t+20s, t+40s.
/// </summary>
public sealed record StartTripleTestCommand(
    string OriginAddress,
    string DestinationAddress,
    RouteProvider Provider,
    DateTimeOffset? StartAt) : IRequest<StartTripleTestResult>;

public sealed record StartTripleTestResult(
    bool IsSuccess,
    string? ErrorCode,
    Guid? SessionId);

public sealed class StartTripleTestValidator : AbstractValidator<StartTripleTestCommand>
{
    public StartTripleTestValidator()
    {
        RuleFor(x => x.OriginAddress).NotEmpty().MaximumLength(500);
        RuleFor(x => x.DestinationAddress).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Provider).IsInEnum();
        RuleFor(x => x.StartAt)
            .Must(t => t is null || t.Value > DateTimeOffset.UtcNow.AddSeconds(-5))
            .WithMessage("Start time must not be in the past.");
    }
}

public sealed class StartTripleTestCommandHandler : IRequestHandler<StartTripleTestCommand, StartTripleTestResult>
{
    private readonly PoTrafficDbContext _db;
    private readonly ITrafficProviderFactory _providerFactory;
    private readonly IBackgroundJobClient _jobClient;
    private readonly ILogger<StartTripleTestCommandHandler> _logger;

    public StartTripleTestCommandHandler(
        PoTrafficDbContext db,
        ITrafficProviderFactory providerFactory,
        IBackgroundJobClient jobClient,
        ILogger<StartTripleTestCommandHandler> logger)
    {
        _db = db;
        _providerFactory = providerFactory;
        _jobClient = jobClient;
        _logger = logger;
    }

    public async Task<StartTripleTestResult> Handle(StartTripleTestCommand cmd, CancellationToken ct)
    {
        // Factory pattern — see ITrafficProviderFactory
        ITrafficProvider provider = _providerFactory.GetProvider(cmd.Provider);

        string? originCoords = await provider.GeocodeAsync(cmd.OriginAddress, ct);
        if (originCoords is null)
        {
            _logger.LogWarning("StartTripleTest: geocode failed for origin '{Origin}'", cmd.OriginAddress);
            return new StartTripleTestResult(false, "GEOCODE_FAILED_ORIGIN", null);
        }

        string? destCoords = await provider.GeocodeAsync(cmd.DestinationAddress, ct);
        if (destCoords is null)
        {
            _logger.LogWarning("StartTripleTest: geocode failed for destination '{Dest}'", cmd.DestinationAddress);
            return new StartTripleTestResult(false, "GEOCODE_FAILED_DESTINATION", null);
        }

        if (originCoords == destCoords)
            return new StartTripleTestResult(false, "SAME_COORDINATES", null);

        DateTimeOffset scheduledAt = cmd.StartAt ?? DateTimeOffset.UtcNow;

        var session = new TripleTestSession
        {
            Id = Guid.NewGuid(),
            OriginAddress = cmd.OriginAddress,
            OriginCoordinates = originCoords,
            DestinationAddress = cmd.DestinationAddress,
            DestinationCoordinates = destCoords,
            Provider = (int)cmd.Provider,
            ScheduledAt = scheduledAt,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Create the 3 shot stubs — results written back by TripleTestShotJob
        int[] offsets = [0, 20, 40];
        for (int i = 0; i < 3; i++)
        {
            session.Shots.Add(new TripleTestShot
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                ShotIndex = i,
                OffsetSeconds = offsets[i]
            });
        }

        _db.TripleTestSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        // Schedule 3 independent Hangfire jobs — mirrors PollRouteJob scheduling pattern
        TimeSpan startDelay = scheduledAt - DateTimeOffset.UtcNow;
        if (startDelay < TimeSpan.Zero) startDelay = TimeSpan.Zero;

        _jobClient.Schedule<TripleTestShotJob>(j => j.Execute(session.Id, 0), startDelay);
        _jobClient.Schedule<TripleTestShotJob>(j => j.Execute(session.Id, 1), startDelay + TimeSpan.FromSeconds(20));
        _jobClient.Schedule<TripleTestShotJob>(j => j.Execute(session.Id, 2), startDelay + TimeSpan.FromSeconds(40));

        _logger.LogInformation(
            "StartTripleTest: session {SessionId} scheduled at {ScheduledAt} for {Origin} → {Dest}",
            session.Id, scheduledAt, cmd.OriginAddress, cmd.DestinationAddress);

        return new StartTripleTestResult(true, null, session.Id);
    }
}
