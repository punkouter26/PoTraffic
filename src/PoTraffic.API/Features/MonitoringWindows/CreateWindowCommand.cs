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
    byte DaysOfWeekMask) : IRequest<CreateWindowResult>;

public sealed record CreateWindowResult(
    bool IsSuccess,
    string? ErrorCode,   // RouteErrorCodes.NotFound | "WINDOW_ALREADY_ACTIVE"
    WindowId? WindowId);

public sealed class CreateWindowValidator : AbstractValidator<CreateWindowCommand>
{
    public CreateWindowValidator()
    {
        RuleFor(x => x.EndTime)
            .GreaterThan(x => x.StartTime)
            .WithMessage("EndTime must be after StartTime.");
        RuleFor(x => x.DaysOfWeekMask)
            .GreaterThan((byte)0)
            .WithMessage("At least one day must be selected.");
    }
}

public sealed class CreateWindowCommandHandler : IRequestHandler<CreateWindowCommand, CreateWindowResult>
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

    public async Task<CreateWindowResult> Handle(CreateWindowCommand cmd, CancellationToken ct)
    {
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
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Add(window);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("MonitoringWindow {WindowId} created for route {RouteId}", window.Id, cmd.RouteId);
        return new CreateWindowResult(true, null, window.Id);
    }
}
