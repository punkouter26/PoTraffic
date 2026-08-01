using FluentValidation;
using Microsoft.Extensions.Logging;
using PoTraffic.API.Infrastructure.Logging;
using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.API.Infrastructure.Scheduling;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Admin;

/// <summary>
/// Geocodes the supplied addresses, persists a TripleTestSession with 3 shot stubs,
/// then schedules 3 jobs at t=0s, t+20s, t+40s.
/// </summary>
public sealed record StartTripleTestCommand(
    string OriginAddress,
    string DestinationAddress,
    RouteProvider Provider,
    DateTimeOffset? StartAt) : IRequest<StartTripleTestResult>;

public sealed record StartTripleTestResult(
    bool IsSuccess,
    string? ErrorCode,
    TripleTestSessionId? SessionId);

public sealed class StartTripleTestValidator : AbstractValidator<StartTripleTestCommand>
{
    public StartTripleTestValidator()
    {
        RuleFor(x => x.OriginAddress).NotEmpty().MaximumLength(ValidationConstants.AddressMaxLength);
        RuleFor(x => x.DestinationAddress).NotEmpty().MaximumLength(ValidationConstants.AddressMaxLength);
        RuleFor(x => x.Provider).IsInEnum();
        RuleFor(x => x.StartAt)
            .Must(t => t is null || t.Value > DateTimeOffset.UtcNow.AddSeconds(-5))
            .WithMessage("Start time must not be in the past.");
    }
}

public sealed class StartTripleTestCommandHandler : IRequestHandler<StartTripleTestCommand, StartTripleTestResult>
{
    private readonly TableStorageContext _db;
    private readonly ITrafficProviderFactory _providerFactory;
    private readonly IJobScheduler _scheduler;
    private readonly ILogger<StartTripleTestCommandHandler> _logger;

    public StartTripleTestCommandHandler(
        TableStorageContext db,
        ITrafficProviderFactory providerFactory,
        IJobScheduler scheduler,
        ILogger<StartTripleTestCommandHandler> logger)
    {
        _db = db;
        _providerFactory = providerFactory;
        _scheduler = scheduler;
        _logger = logger;
    }

    public async Task<StartTripleTestResult> Handle(StartTripleTestCommand cmd, CancellationToken ct)
    {
        // Factory pattern — see ITrafficProviderFactory
        ITrafficProvider provider = _providerFactory.GetProvider(cmd.Provider);

        string? originCoords = await provider.GeocodeAsync(cmd.OriginAddress, ct);
        if (originCoords is null)
        {
            _logger.LogWarning("StartTripleTest: geocode failed for origin {AddressRef}", PiiRedactor.Redact(cmd.OriginAddress));
            return new StartTripleTestResult(false, "GEOCODE_FAILED_ORIGIN", null);
        }

        string? destCoords = await provider.GeocodeAsync(cmd.DestinationAddress, ct);
        if (destCoords is null)
        {
            _logger.LogWarning("StartTripleTest: geocode failed for destination {AddressRef}", PiiRedactor.Redact(cmd.DestinationAddress));
            return new StartTripleTestResult(false, "GEOCODE_FAILED_DESTINATION", null);
        }

        if (originCoords == destCoords)
            return new StartTripleTestResult(false, RouteErrorCodes.SameCoordinates, null);

        DateTimeOffset scheduledAt = cmd.StartAt ?? DateTimeOffset.UtcNow;

        var session = new TripleTestSession
        {
            Id = TripleTestSessionId.New(),
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
                Id = TripleTestShotId.New(),
                SessionId = session.Id,
                ShotIndex = i,
                OffsetSeconds = offsets[i]
            });
        }

        _db.Add(session);
        foreach (TripleTestShot shot in session.Shots)
            _db.Add(shot);
        await _db.SaveChangesAsync(ct);

        // Schedule 3 independent jobs — mirrors PollRouteJob scheduling pattern
        TimeSpan startDelay = scheduledAt - DateTimeOffset.UtcNow;
        if (startDelay < TimeSpan.Zero) startDelay = TimeSpan.Zero;

        TripleTestSessionId sessionId = session.Id;
        var shotJob = new TripleTestShotJob(null!, null!);
        _scheduler.Schedule(() => shotJob.Execute(sessionId, 0), startDelay);
        _scheduler.Schedule(() => shotJob.Execute(sessionId, 1), startDelay + TimeSpan.FromSeconds(20));
        _scheduler.Schedule(() => shotJob.Execute(sessionId, 2), startDelay + TimeSpan.FromSeconds(40));

        _logger.LogInformation(
            "StartTripleTest: session {SessionId} scheduled at {ScheduledAt} for {OriginRef} → {DestRef}",
            session.Id, scheduledAt, PiiRedactor.Redact(cmd.OriginAddress), PiiRedactor.Redact(cmd.DestinationAddress));

        return new StartTripleTestResult(true, null, session.Id);
    }
}
