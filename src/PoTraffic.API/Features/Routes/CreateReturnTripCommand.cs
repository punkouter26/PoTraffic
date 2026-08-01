using Microsoft.Extensions.Logging;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Routes;

/// <summary>
/// Creates the reverse-direction counterpart of an existing route and links the two (#3).
/// Origin/destination (and their already-geocoded coordinates) are swapped, so no extra
/// geocoding call is made. The return route starts with NO monitoring window — the user
/// sets its own schedule (evening commutes rarely mirror the morning window). Idempotent:
/// if a return trip already exists it is returned unchanged.
/// </summary>
public sealed record CreateReturnTripCommand(RouteId RouteId, UserId UserId)
    : IRequest<CreateRouteResult>;

public sealed class CreateReturnTripCommandHandler(
    Infrastructure.Storage.TableStorageContext db,
    ILogger<CreateReturnTripCommandHandler> logger)
    : IRequestHandler<CreateReturnTripCommand, CreateRouteResult>
{
    public async Task<CreateRouteResult> Handle(CreateReturnTripCommand cmd, CancellationToken ct)
    {
        EntityRoute? source = db.GetOwnedRoute(cmd.RouteId, cmd.UserId, excludeDeleted: true);
        if (source is null)
            return new CreateRouteResult(false, RouteErrorCodes.NotFound, null);

        // Idempotent — return the existing linked route if it's still live.
        if (source.ReturnRouteId is RouteId existingId)
        {
            EntityRoute? existing = db.Routes.FirstOrDefault(r =>
                r.Id == existingId && r.MonitoringStatus != (int)MonitoringStatus.Deleted);
            if (existing is not null)
                return new CreateRouteResult(true, null, CreateRouteCommandHandler.MapToDto(existing));
        }

        var reverse = new EntityRoute
        {
            Id = RouteId.New(),
            UserId = source.UserId,
            OriginAddress = source.DestinationAddress,
            OriginCoordinates = source.DestinationCoordinates,
            DestinationAddress = source.OriginAddress,
            DestinationCoordinates = source.OriginCoordinates,
            Provider = source.Provider,
            MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            ReturnRouteId = source.Id
        };

        db.Add(reverse);
        source.ReturnRouteId = reverse.Id;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Return trip {ReturnRouteId} created and linked to route {RouteId}", reverse.Id, source.Id);
        return new CreateRouteResult(true, null, CreateRouteCommandHandler.MapToDto(reverse));
    }
}
