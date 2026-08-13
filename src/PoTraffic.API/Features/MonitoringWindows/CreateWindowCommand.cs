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
        // The client converts local wall-clock times to UTC before submitting
        // (CreateRoutePage.ToUtcHHmm). For Eastern-time users picking e.g.
        // 09:21–21:21 local, the UTC conversion produces 13:21–01:21 — a
        // window that spans midnight UTC. Allow EndTime <= StartTime (wrap-around)
        // and only reject the degenerate EndTime == StartTime case, which would
        // produce zero polling slots.
        RuleFor(x => x.EndTime)
            .NotEqual(x => x.StartTime)
            .WithMessage("EndTime must be different from StartTime.");
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
