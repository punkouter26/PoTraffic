using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Shared.Enums;
namespace PoTraffic.Api.Features.Routes;

/// <summary>
/// Returns live travel time for a route WITHOUT persisting a PollRecord or consuming quota (FR-016).
/// </summary>
public sealed record CheckNowCommand(
    Guid RouteId,
    Guid UserId) : IRequest<CheckNowResult>;

public sealed record CheckNowResult(
    bool IsSuccess,
    int? DurationSeconds,
    int? DistanceMetres,
    string? ErrorCode);

// Command pattern — encapsulates transient provider call as a discrete MediatR command
public sealed class CheckNowCommandHandler : IRequestHandler<CheckNowCommand, CheckNowResult>
{
    private readonly PoTrafficDbContext _db;
    private readonly ITrafficProviderFactory _providerFactory;
    private readonly ILogger<CheckNowCommandHandler> _logger;

    public CheckNowCommandHandler(
        PoTrafficDbContext db,
        ITrafficProviderFactory providerFactory,
        ILogger<CheckNowCommandHandler> logger)
    {
        _db = db;
        _providerFactory = providerFactory;
        _logger = logger;
    }

    public async Task<CheckNowResult> Handle(CheckNowCommand command, CancellationToken ct)
    {
        // Verify ownership
        EntityRoute? route = await _db.Set<EntityRoute>()
            .FirstOrDefaultAsync(r => r.Id == command.RouteId && r.UserId == command.UserId, ct);

        if (route is null)
            return new CheckNowResult(false, null, null, "NOT_FOUND");

        ITrafficProvider provider = _providerFactory.GetProvider((RouteProvider)route.Provider);
        TravelResult? travel = await provider.GetTravelTimeAsync(
            route.OriginCoordinates!, route.DestinationCoordinates!);

        if (travel is null)
        {
            _logger.LogWarning("CheckNow provider returned null for route {RouteId}", command.RouteId);
            return new CheckNowResult(false, null, null, "PROVIDER_ERROR");
        }

        // FR-016: no PollRecord inserted, no quota consumed
        return new CheckNowResult(true, travel.DurationSeconds, travel.DistanceMetres, null);
    }
}
