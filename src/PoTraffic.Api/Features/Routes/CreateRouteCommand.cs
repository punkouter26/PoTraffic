using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.DTOs.Routes;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Routes;

/// <summary>Creates a new route after address verification. Strategy pattern — ITrafficProvider swaps mapping API per route.Provider.</summary>
public sealed record CreateRouteCommand(
    Guid UserId,
    string OriginAddress,
    string DestinationAddress,
    RouteProvider Provider,
    string StartTime = "07:00",
    string EndTime = "09:00",
    byte DaysOfWeekMask = 0x1F) : IRequest<CreateRouteResult>;

public sealed record CreateRouteResult(
    bool IsSuccess,
    string? ErrorCode,   // "SAME_COORDINATES" | "GEOCODE_FAILED"
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

        RuleFor(x => x.StartTime).NotEmpty().Matches(TimePattern)
            .WithMessage("Start time must be in HH:mm format.");
        RuleFor(x => x.EndTime).NotEmpty().Matches(TimePattern)
            .WithMessage("End time must be in HH:mm format.");
        RuleFor(x => x.EndTime)
            .Must((cmd, endTime) =>
            {
                if (!TimeOnly.TryParse(cmd.StartTime, out TimeOnly s)) return true;
                if (!TimeOnly.TryParse(endTime, out TimeOnly e)) return true;
                return e > s;
            })
            .WithMessage("End time must be after start time.");
        RuleFor(x => x.DaysOfWeekMask)
            .Must(m => m > 0)
            .WithMessage("At least one day of week must be selected.");
    }
}

public sealed class CreateRouteCommandHandler(
    PoTrafficDbContext db,
    ITrafficProviderFactory providerFactory,
    ILogger<CreateRouteCommandHandler> logger) : IRequestHandler<CreateRouteCommand, CreateRouteResult>
{
    public async Task<CreateRouteResult> Handle(CreateRouteCommand cmd, CancellationToken ct)
    {
        // Strategy pattern — select provider via factory (resolves keyed DI lookup)
        ITrafficProvider provider = providerFactory.GetProvider(cmd.Provider);

        string? originCoords = await provider.GeocodeAsync(cmd.OriginAddress, ct);
        if (originCoords is null)
        {
            logger.LogWarning("Geocode failed for origin address {Address}", cmd.OriginAddress);
            return new CreateRouteResult(false, "GEOCODE_FAILED", null);
        }

        string? destCoords = await provider.GeocodeAsync(cmd.DestinationAddress, ct);
        if (destCoords is null)
        {
            logger.LogWarning("Geocode failed for destination address {Address}", cmd.DestinationAddress);
            return new CreateRouteResult(false, "GEOCODE_FAILED", null);
        }

        if (originCoords == destCoords)
            return new CreateRouteResult(false, "SAME_COORDINATES", null);

        var route = new EntityRoute
        {
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
            StartTime = TimeOnly.Parse(cmd.StartTime),
            EndTime = TimeOnly.Parse(cmd.EndTime),
            DaysOfWeekMask = cmd.DaysOfWeekMask,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.Routes.Add(route);
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
        r.HangfireJobChainId,
        r.CreatedAt,
        r.Windows.Select(w => new MonitoringWindowDto(
            w.Id,
            w.StartTime.ToString("HH:mm"),
            w.EndTime.ToString("HH:mm"),
            DecodeDaysOfWeek(w.DaysOfWeekMask),
            w.IsActive)).ToList());

    private static IReadOnlyList<string> DecodeDaysOfWeek(byte mask)
    {
        string[] names = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
        var days = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            if ((mask & (1 << i)) != 0)
                days.Add(names[i]);
        }
        return days;
    }
}
