using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Shared.DTOs.Routes;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Routes;

public sealed record UpdateRouteCommand(
    Guid RouteId,
    Guid UserId,
    string? OriginAddress,
    string? DestinationAddress,
    RouteProvider? Provider) : IRequest<UpdateRouteResult>;

public sealed record UpdateRouteResult(
    bool IsSuccess,
    string? ErrorCode,   // "NOT_FOUND" | "GEOCODE_FAILED" | "SAME_COORDINATES"
    RouteDto? Route);

public sealed class UpdateRouteCommandHandler(
    PoTrafficDbContext db,
    ITrafficProviderFactory providerFactory,
    IBackgroundJobClient jobClient,
    ILogger<UpdateRouteCommandHandler> logger) : IRequestHandler<UpdateRouteCommand, UpdateRouteResult>
{
    public async Task<UpdateRouteResult> Handle(UpdateRouteCommand cmd, CancellationToken ct)
    {
        EntityRoute? route = await db.Routes
            .Include(r => r.Windows)
            .FirstOrDefaultAsync(r => r.Id == cmd.RouteId && r.UserId == cmd.UserId
                && r.MonitoringStatus != (int)MonitoringStatus.Deleted, ct);

        if (route is null)
            return new UpdateRouteResult(false, "NOT_FOUND", null);

        RouteProvider effectiveProvider = cmd.Provider ?? (RouteProvider)route.Provider;
        ITrafficProvider provider = providerFactory.GetProvider(effectiveProvider);

        // Re-geocode only changed addresses
        if (cmd.OriginAddress is not null)
        {
            string? coords = await provider.GeocodeAsync(cmd.OriginAddress, ct);
            if (coords is null)
                return new UpdateRouteResult(false, "GEOCODE_FAILED", null);
            route.OriginAddress = cmd.OriginAddress;
            route.OriginCoordinates = coords;
        }

        if (cmd.DestinationAddress is not null)
        {
            string? coords = await provider.GeocodeAsync(cmd.DestinationAddress, ct);
            if (coords is null)
                return new UpdateRouteResult(false, "GEOCODE_FAILED", null);
            route.DestinationAddress = cmd.DestinationAddress;
            route.DestinationCoordinates = coords;
        }

        if (route.OriginCoordinates == route.DestinationCoordinates)
            return new UpdateRouteResult(false, "SAME_COORDINATES", null);

        // Cancel + restart Hangfire chain if provider changes
        if (cmd.Provider.HasValue && (int)cmd.Provider.Value != route.Provider)
        {
            if (route.HangfireJobChainId is not null)
            {
                jobClient.Delete(route.HangfireJobChainId);
                logger.LogInformation("Cancelled Hangfire job chain {JobId} for route {RouteId} due to provider change",
                    route.HangfireJobChainId, route.Id);
            }
            route.Provider = (int)cmd.Provider.Value;
            route.HangfireJobChainId = null;
        }

        await db.SaveChangesAsync(ct);
        return new UpdateRouteResult(true, null, CreateRouteCommandHandler.MapToDto(route));
    }
}
