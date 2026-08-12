using Microsoft.Extensions.Logging;
using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Routes;

/// <summary>
/// Returns live travel time for a route WITHOUT persisting a PollRecord or consuming quota (FR-016).
/// </summary>
public sealed record CheckNowCommand(
    RouteId RouteId,
    UserId UserId) : IRequest<CheckNowResult>;

public sealed record CheckNowResult(
    bool IsSuccess,
    int? DurationSeconds,
    int? DistanceMetres,
    string? ErrorCode);

// Command pattern — encapsulates transient provider call as a discrete dispatcher command
public sealed class CheckNowCommandHandler(
    TableStorageContext db,
    ITrafficProviderFactory providerFactory,
    ILogger<CheckNowCommandHandler> logger) : IRequestHandler<CheckNowCommand, CheckNowResult>
{
    public async Task<CheckNowResult> Handle(CheckNowCommand command, CancellationToken ct)
    {
        EntityRoute? route = db.GetOwnedRoute(command.RouteId, command.UserId, excludeDeleted: true);

        if (route is null)
            return new CheckNowResult(false, null, null, RouteErrorCodes.NotFound);

        // The sample route's history is synthetic (#10). Polling it live would bill a real
        // provider call to illustrate a fiction, and drop a genuine sample into a series
        // the UI labels as demo data.
        if (route.IsDemo)
            return new CheckNowResult(false, null, null, RouteErrorCodes.DemoRoute);

        ITrafficProvider provider = providerFactory.GetProvider((RouteProvider)route.Provider);
        TravelResult? travel = await provider.GetTravelTimeAsync(
            route.OriginCoordinates!, route.DestinationCoordinates!, ct);

        if (travel is null)
        {
            logger.LogWarning("CheckNow provider returned null for route {RouteId}", command.RouteId);
            return new CheckNowResult(false, null, null, "PROVIDER_ERROR");
        }

        // FR-016: no PollRecord inserted, no quota consumed
        return new CheckNowResult(true, travel.DurationSeconds, travel.DistanceMetres, null);
    }
}
