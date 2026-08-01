using PoTraffic.API.Infrastructure.Scheduling;
using PoTraffic.API.Infrastructure.Storage;


using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;



using PoTraffic.API.Infrastructure.Providers;

using PoTraffic.Shared.DTOs.Routes;

using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Routes;

public sealed record UpdateRouteCommand(
    RouteId RouteId,
    UserId UserId,
    string? OriginAddress,
    string? DestinationAddress,
    RouteProvider? Provider) : IRequest<UpdateRouteResult>;

public sealed record UpdateRouteResult(
    bool IsSuccess,
    string? ErrorCode,   // RouteErrorCodes: NOT_FOUND | GEOCODE_FAILED | SAME_COORDINATES
    RouteDto? Route);

public sealed class UpdateRouteCommandHandler(
    TableStorageContext db,
    ITrafficProviderFactory providerFactory,
    IJobScheduler scheduler,
    ILogger<UpdateRouteCommandHandler> logger) : IRequestHandler<UpdateRouteCommand, UpdateRouteResult>
{
    public async Task<UpdateRouteResult> Handle(UpdateRouteCommand cmd, CancellationToken ct)
    {
        EntityRoute? route = db.GetOwnedRoute(cmd.RouteId, cmd.UserId, excludeDeleted: true);

        if (route is null)
            return new UpdateRouteResult(false, RouteErrorCodes.NotFound, null);

        RouteProvider effectiveProvider = cmd.Provider ?? (RouteProvider)route.Provider;
        ITrafficProvider provider = providerFactory.GetProvider(effectiveProvider);

        // Re-geocode only changed addresses
        if (cmd.OriginAddress is not null)
        {
            string? coords = await provider.GeocodeAsync(cmd.OriginAddress, ct);
            if (coords is null)
                return new UpdateRouteResult(false, RouteErrorCodes.GeocodeFailed, null);
            route.OriginAddress = cmd.OriginAddress;
            route.OriginCoordinates = coords;
        }

        if (cmd.DestinationAddress is not null)
        {
            string? coords = await provider.GeocodeAsync(cmd.DestinationAddress, ct);
            if (coords is null)
                return new UpdateRouteResult(false, RouteErrorCodes.GeocodeFailed, null);
            route.DestinationAddress = cmd.DestinationAddress;
            route.DestinationCoordinates = coords;
        }

        if (route.OriginCoordinates == route.DestinationCoordinates)
            return new UpdateRouteResult(false, RouteErrorCodes.SameCoordinates, null);

        // Cancel + restart job chain if provider changes
        if (cmd.Provider.HasValue && (int)cmd.Provider.Value != route.Provider)
        {
            if (route.JobChainId is not null)
            {
                scheduler.Cancel(route.JobChainId);
                logger.LogInformation("Cancelled job chain {JobId} for route {RouteId} due to provider change",
                    route.JobChainId, route.Id);
            }
            route.Provider = (int)cmd.Provider.Value;
            route.JobChainId = null;
        }

        await db.SaveChangesAsync(ct);
        return new UpdateRouteResult(true, null, CreateRouteCommandHandler.MapToDto(route));
    }
}
