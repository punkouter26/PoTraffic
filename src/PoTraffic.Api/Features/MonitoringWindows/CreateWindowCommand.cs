using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.MonitoringWindows;

public sealed record CreateWindowCommand(
    Guid RouteId,
    Guid UserId,
    TimeOnly StartTime,
    TimeOnly EndTime,
    byte DaysOfWeekMask) : IRequest<CreateWindowResult>;

public sealed record CreateWindowResult(
    bool IsSuccess,
    string? ErrorCode,   // "NOT_FOUND" | "WINDOW_ALREADY_ACTIVE"
    Guid? WindowId);

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
    private readonly PoTrafficDbContext _db;
    private readonly ILogger<CreateWindowCommandHandler> _logger;

    public CreateWindowCommandHandler(
        PoTrafficDbContext db,
        ILogger<CreateWindowCommandHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<CreateWindowResult> Handle(CreateWindowCommand cmd, CancellationToken ct)
    {
        // Verify route ownership
        bool routeExists = await _db.Routes.AnyAsync(
            r => r.Id == cmd.RouteId
                && r.UserId == cmd.UserId
                && r.MonitoringStatus != (int)MonitoringStatus.Deleted, ct);

        if (!routeExists)
            return new CreateWindowResult(false, "NOT_FOUND", null);

        // Only one active window per route is supported
        bool activeWindowExists = await _db.MonitoringWindows
            .AnyAsync(w => w.RouteId == cmd.RouteId && w.IsActive, ct);

        if (activeWindowExists)
            return new CreateWindowResult(false, "WINDOW_ALREADY_ACTIVE", null);

        var window = new MonitoringWindow
        {
            RouteId = cmd.RouteId,
            StartTime = cmd.StartTime,
            EndTime = cmd.EndTime,
            DaysOfWeekMask = cmd.DaysOfWeekMask,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.MonitoringWindows.Add(window);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("MonitoringWindow {WindowId} created for route {RouteId}", window.Id, cmd.RouteId);
        return new CreateWindowResult(true, null, window.Id);
    }
}
