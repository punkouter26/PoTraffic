using FluentValidation;
using PoTraffic.API.Infrastructure.Logging;
using PoTraffic.API.Infrastructure.Storage;


using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;



using PoTraffic.API.Infrastructure.Providers;

using PoTraffic.Shared.Constants;

using PoTraffic.Shared.DTOs.Routes;

using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Routes;

/// <summary>Creates a new route after address verification. Strategy pattern — ITrafficProvider swaps mapping API per route.Provider.</summary>
public sealed record CreateRouteCommand(
    UserId UserId,
    string OriginAddress,
    string DestinationAddress,
    RouteProvider Provider,
    string StartTime = "07:00",
    string EndTime = "09:00",
    byte DaysOfWeekMask = 0x1F) : IRequest<CreateRouteResult>;

public sealed record CreateRouteResult(
    bool IsSuccess,
    string? ErrorCode,   // RouteErrorCodes: MAPS_KEY_MISSING | ADDRESS_NOT_FOUND | SAME_COORDINATES
    RouteDto? Route);

public sealed class CreateRouteValidator : AbstractValidator<CreateRouteCommand>
{
    private static readonly System.Text.RegularExpressions.Regex TimePattern =
        new(@"^\d{2}:\d{2}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public CreateRouteValidator()
    {
        RuleFor(x => x.OriginAddress).NotEmpty().MaximumLength(ValidationConstants.AddressMaxLength);
        RuleFor(x => x.DestinationAddress).NotEmpty().MaximumLength(ValidationConstants.AddressMaxLength);
        RuleFor(x => x.Provider).IsInEnum();

        // Two gates: the regex enforces the canonical HH:mm shape, and TryParse rejects
        // shapes that pass the regex but aren't real times (e.g. "99:99") — which the
        // handler's TimeOnly.Parse would otherwise throw a FormatException on (→ 500).
        RuleFor(x => x.StartTime).NotEmpty()
            .Matches(TimePattern).WithMessage("Start time must be in HH:mm format.")
            .Must(t => TimeOnly.TryParse(t, out _)).WithMessage("Start time must be a valid time of day.");
        RuleFor(x => x.EndTime).NotEmpty()
            .Matches(TimePattern).WithMessage("End time must be in HH:mm format.")
            .Must(t => TimeOnly.TryParse(t, out _)).WithMessage("End time must be a valid time of day.");
        RuleFor(x => x.EndTime)
            .Must((cmd, endTime) =>
            {
                if (!TimeOnly.TryParse(cmd.StartTime, out TimeOnly s)) return true;
                if (!TimeOnly.TryParse(endTime, out TimeOnly e)) return true;
                return e > s;
            })
            .WithMessage("End time must be after start time.");
        // Only the low 7 bits map to days (Mon–Sun); a high-bit-only mask like 0x80
        // would pass `m > 0` yet decode to zero real days and silently stall polling.
        RuleFor(x => x.DaysOfWeekMask)
            .Must(m => (m & 0x7F) != 0)
            .WithMessage("At least one day of week must be selected.");
    }
}

public sealed class CreateRouteCommandHandler(
    TableStorageContext db,
    ITrafficProviderFactory providerFactory,
    ILogger<CreateRouteCommandHandler> logger) : IRequestHandler<CreateRouteCommand, CreateRouteResult>
{
    public async Task<CreateRouteResult> Handle(CreateRouteCommand cmd, CancellationToken ct)
    {
        // Strategy pattern — select provider via factory (resolves keyed DI lookup)
        ITrafficProvider provider = providerFactory.GetProvider(cmd.Provider);

        // A GeocodingConfigurationException (server misconfiguration, e.g. a missing Maps key)
        // is deliberately NOT caught here — GlobalExceptionHandler maps it to the same 422 +
        // MAPS_KEY_MISSING for every geocoding call site, not just this one.
        string? originCoords = await provider.GeocodeAsync(cmd.OriginAddress, ct);
        if (originCoords is null)
        {
            logger.LogWarning("Geocode failed for origin address {AddressRef}", PiiRedactor.Redact(cmd.OriginAddress));
            return new CreateRouteResult(false, RouteErrorCodes.AddressNotFound, null);
        }

        string? destCoords = await provider.GeocodeAsync(cmd.DestinationAddress, ct);
        if (destCoords is null)
        {
            logger.LogWarning("Geocode failed for destination address {AddressRef}", PiiRedactor.Redact(cmd.DestinationAddress));
            return new CreateRouteResult(false, RouteErrorCodes.AddressNotFound, null);
        }

        if (originCoords == destCoords)
            return new CreateRouteResult(false, RouteErrorCodes.SameCoordinates, null);

        var route = new EntityRoute
        {
            Id = RouteId.New(),
            UserId = cmd.UserId,
            OriginAddress = cmd.OriginAddress,
            OriginCoordinates = originCoords,
            DestinationAddress = cmd.DestinationAddress,
            DestinationCoordinates = destCoords,
            Provider = (int)cmd.Provider,
            MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Add the initial monitoring window inline so the route is immediately schedulable.
        route.Windows.Add(new MonitoringWindow
        {
            Id = WindowId.New(),
            RouteId = route.Id,
            StartTime = TimeOnly.Parse(cmd.StartTime),
            EndTime = TimeOnly.Parse(cmd.EndTime),
            DaysOfWeekMask = cmd.DaysOfWeekMask,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.Add(route);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Route {RouteId} created for user {UserId} with monitoring window {Start}–{End}",
            route.Id, cmd.UserId, cmd.StartTime, cmd.EndTime);

        RouteDto dto = MapToDto(route);
        return new CreateRouteResult(true, null, dto);
    }

    internal static RouteDto MapToDto(EntityRoute r) => new(
        r.Id,
        r.OriginAddress,
        r.OriginCoordinates,
        r.DestinationAddress,
        r.DestinationCoordinates,
        (RouteProvider)r.Provider,
        (MonitoringStatus)r.MonitoringStatus,
        r.JobChainId,
        r.CreatedAt,
        r.Windows.Select(w => w.ToDto()).ToList(),
        r.ReturnRouteId);
}
