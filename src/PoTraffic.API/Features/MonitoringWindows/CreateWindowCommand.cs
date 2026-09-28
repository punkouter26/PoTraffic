using PoTraffic.API.Infrastructure.Time;
using FluentValidation;
using PoTraffic.API.Infrastructure.Storage;


using Microsoft.Extensions.Logging;



using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.MonitoringWindows;

public sealed record CreateWindowCommand(
    RouteId RouteId,
    UserId UserId,
    TimeOnly StartTime,
    TimeOnly EndTime,
    byte DaysOfWeekMask,
    string? TimeZoneId = null);

public sealed record CreateWindowResult(
    bool IsSuccess,
    string? ErrorCode,   // RouteErrorCodes.NotFound | "WINDOW_ALREADY_ACTIVE"
    WindowId? WindowId);

public sealed class CreateWindowValidator : AbstractValidator<CreateWindowCommand>
{
    public CreateWindowValidator()
    {
        // A window may wrap midnight (22:00–02:00 local, or a legacy window the client
        // converted to UTC). Allow EndTime < StartTime and only reject the degenerate
        // EndTime == StartTime case, which would produce zero polling slots.
        RuleFor(x => x.EndTime)
            .NotEqual(x => x.StartTime)
            .WithMessage("EndTime must be different from StartTime.");
        RuleFor(x => x.DaysOfWeekMask)
            .GreaterThan((byte)0)
            .WithMessage("At least one day must be selected.");
        RuleFor(x => x.TimeZoneId)
            .Must(z => z is null || UserTime.TryFindZone(z) is not null)
            .WithMessage("Unknown time zone.");
    }
}

public sealed class CreateWindowCommandHandler
{
    private readonly TableStorageContext _db;
    private readonly ILogger<CreateWindowCommandHandler> _logger;

    public CreateWindowCommandHandler(
        TableStorageContext db,
        ILogger<CreateWindowCommandHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    private static readonly CreateWindowValidator Validator = new();

    public async Task<CreateWindowResult> Handle(CreateWindowCommand cmd, CancellationToken ct)
    {
        await Validator.ValidateAndThrowAsync(cmd, ct);

        // Verify route ownership
        if (!_db.OwnsRoute(cmd.RouteId, cmd.UserId, excludeDeleted: true))
            return new CreateWindowResult(false, RouteErrorCodes.NotFound, null);

        // Only one active window per route is supported
        bool activeWindowExists = _db.MonitoringWindows
            .Any(w => w.RouteId == cmd.RouteId && w.IsActive);

        if (activeWindowExists)
            return new CreateWindowResult(false, "WINDOW_ALREADY_ACTIVE", null);

        var window = new MonitoringWindow
        {
            Id = WindowId.New(),
            RouteId = cmd.RouteId,
            StartTime = cmd.StartTime,
            EndTime = cmd.EndTime,
            DaysOfWeekMask = cmd.DaysOfWeekMask,
            TimeZoneId = cmd.TimeZoneId,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Add() only cascades parent → child, and route.Windows is otherwise linked only at
        // hydration — without this the RouteDto never shows the new window until a restart,
        // so an edited schedule vanished from the route page and the next save hit 409.
        _db.Routes.First(r => r.Id == cmd.RouteId).Windows.Add(window);
        _db.Add(window);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("MonitoringWindow {WindowId} created for route {RouteId}", window.Id, cmd.RouteId);
        return new CreateWindowResult(true, null, window.Id);
    }
}
