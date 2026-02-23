using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Admin;

/// <summary>
/// Hangfire job that fires a single shot of a Triple Test session.
/// Mirrors PollRouteJob — resolved via DI scope, writes result back to DB.
/// </summary>
public sealed class TripleTestShotJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TripleTestShotJob> _logger;

    public TripleTestShotJob(
        IServiceScopeFactory scopeFactory,
        ILogger<TripleTestShotJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task Execute(Guid sessionId, int shotIndex)
    {
        _logger.LogInformation(
            "TripleTestShotJob executing session {SessionId} shot {ShotIndex}", sessionId, shotIndex);

        using IServiceScope scope = _scopeFactory.CreateScope();
        PoTrafficDbContext db = scope.ServiceProvider.GetRequiredService<PoTrafficDbContext>();

        TripleTestSession? session = await db.TripleTestSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        if (session is null)
        {
            _logger.LogWarning(
                "TripleTestShotJob: session {SessionId} not found — skipping shot {ShotIndex}",
                sessionId, shotIndex);
            return;
        }

        // Strategy pattern — select provider by enum key
        RouteProvider provider = (RouteProvider)session.Provider;
        ITrafficProvider trafficProvider = scope.ServiceProvider
            .GetRequiredKeyedService<ITrafficProvider>(provider);

        TripleTestShot? shot = await db.TripleTestShots
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.ShotIndex == shotIndex);

        if (shot is null)
        {
            _logger.LogWarning(
                "TripleTestShotJob: shot record missing for session {SessionId} shot {ShotIndex}",
                sessionId, shotIndex);
            return;
        }

        shot.FiredAt = DateTimeOffset.UtcNow;

        try
        {
            TravelResult? result = await trafficProvider.GetTravelTimeAsync(
                session.OriginCoordinates, session.DestinationCoordinates);

            if (result is null)
            {
                shot.IsSuccess = false;
                shot.ErrorCode = "PROVIDER_ERROR";
                _logger.LogWarning(
                    "TripleTestShotJob: provider returned null for session {SessionId} shot {ShotIndex}",
                    sessionId, shotIndex);
            }
            else
            {
                shot.IsSuccess = true;
                shot.DurationSeconds = result.DurationSeconds;
                shot.DistanceMetres = result.DistanceMetres;
            }
        }
        catch (Exception ex)
        {
            shot.IsSuccess = false;
            shot.ErrorCode = "EXCEPTION";
            _logger.LogError(ex,
                "TripleTestShotJob: exception for session {SessionId} shot {ShotIndex}", sessionId, shotIndex);
        }

        await db.SaveChangesAsync();

        _logger.LogInformation(
            "TripleTestShotJob: completed session {SessionId} shot {ShotIndex} — success={IsSuccess} duration={Duration}s",
            sessionId, shotIndex, shot.IsSuccess, shot.DurationSeconds);
    }
}
