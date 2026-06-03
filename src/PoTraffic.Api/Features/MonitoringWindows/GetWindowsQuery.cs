using MediatR;
using PoTraffic.Api.Infrastructure.Storage;


using PoTraffic.Shared.DTOs.Routes;

namespace PoTraffic.Api.Features.MonitoringWindows;

/// <summary>Efficient direct query for a route's windows — avoids loading all user routes.</summary>
public sealed record GetWindowsQuery(Guid RouteId, Guid UserId) : IRequest<IReadOnlyList<MonitoringWindowDto>?>;

public sealed class GetWindowsQueryHandler(TableStorageContext db)
    : IRequestHandler<GetWindowsQuery, IReadOnlyList<MonitoringWindowDto>?>
{
    public async Task<IReadOnlyList<MonitoringWindowDto>?> Handle(GetWindowsQuery q, CancellationToken ct)
    {
        // Verify route ownership before returning windows
        bool routeExists = await db.Routes
            .Any(r => r.Id == q.RouteId && r.UserId == q.UserId);

        if (!routeExists) return null;

        return await db.MonitoringWindows
            .Where(w => w.RouteId == q.RouteId)
            .OrderBy(w => w.StartTime)
            .Select(w => new MonitoringWindowDto(
                w.Id,
                w.StartTime.ToString("HH:mm"),
                w.EndTime.ToString("HH:mm"),
                DecodeDaysOfWeek(w.DaysOfWeekMask),
                w.IsActive))
            .ToList();
    }

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
