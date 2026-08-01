using Microsoft.AspNetCore.Mvc;
using PoTraffic.API.Features.Routes;
using PoTraffic.API.Infrastructure.Security;
using PoTraffic.Shared.DTOs.Routes;

namespace PoTraffic.API.Features.History;

/// <summary>
/// Minimal API group for history / baseline / sessions endpoints.
/// All endpoints require authentication (JWT bearer).
/// </summary>
public static class HistoryEndpoints
{
    public static IEndpointRouteBuilder MapHistoryEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes
            .MapGroup("/api/routes/{routeId:guid}")
            .RequireAuthorization("ProductionMicrosoftAuth")
            .WithTags("History");

        // GET /api/routes/{routeId}/poll-history?page=1&pageSize=20&sinceUtc=2026-04-04T00:00:00Z
        group.MapGet("/poll-history", async (
            RouteId routeId,
            ISender sender,
            HttpContext ctx,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] DateTime? sinceUtc = null) =>
        {
            UserId userId = ctx.User.GetUserId();
            var result = await sender.Send(
                new GetPollHistoryQuery(routeId, userId, page, pageSize, sinceUtc));
            return Results.Ok(result);
        });

        // GET /api/routes/{routeId}/baseline?dayOfWeek=Monday
        group.MapGet("/baseline", async (
            RouteId routeId,
            ISender sender,
            HttpContext ctx,
            [FromQuery] string dayOfWeek = "Monday") =>
        {
            UserId userId = ctx.User.GetUserId();
            var result = await sender.Send(new GetBaselineQuery(routeId, userId, dayOfWeek));
            return Results.Ok(result);
        });

        // GET /api/routes/{routeId}/sessions
        group.MapGet("/sessions", async (
            RouteId routeId,
            ISender sender,
            HttpContext ctx) =>
        {
            UserId userId = ctx.User.GetUserId();
            var result = await sender.Send(new GetSessionsQuery(routeId, userId));
            return Results.Ok(result);
        });

        // GET /api/routes/{routeId}/optimal-departure?dayOfWeek=Monday
        group.MapGet("/optimal-departure", async (
            RouteId routeId,
            ISender sender,
            HttpContext ctx,
            [FromQuery] string dayOfWeek = "Monday") =>
        {
            UserId userId = ctx.User.GetUserId();
            var result = await sender.Send(new GetOptimalDepartureQuery(routeId, userId, dayOfWeek));
            return result is null ? Results.NoContent() : Results.Ok(result);
        });

        // GET /api/routes/{routeId}/weekday-comparison — mean travel time per day-of-week (#4)
        group.MapGet("/weekday-comparison", async (
            RouteId routeId,
            ISender sender,
            HttpContext ctx) =>
        {
            UserId userId = ctx.User.GetUserId();
            var result = await sender.Send(new GetWeekdayComparisonQuery(routeId, userId));
            return Results.Ok(result);
        });

        // GET /api/routes/{routeId} — single route (drives the return-trip link, #3)
        group.MapGet("", async (
            RouteId routeId,
            ISender sender,
            HttpContext ctx) =>
        {
            UserId userId = ctx.User.GetUserId();
            RouteDto? route = await sender.Send(new GetRouteByIdQuery(routeId, userId));
            return route is null ? Results.NotFound() : Results.Ok(route);
        });

        // GET /api/routes/{routeId}/departure.ics?dayOfWeek=Monday — calendar reminder (#2)
        group.MapGet("/departure.ics", async (
            RouteId routeId,
            ISender sender,
            HttpContext ctx,
            [FromQuery] string dayOfWeek = "Monday") =>
        {
            UserId userId = ctx.User.GetUserId();
            RouteDto? route = await sender.Send(new GetRouteByIdQuery(routeId, userId));
            if (route is null) return Results.NotFound();

            var optimal = await sender.Send(new GetOptimalDepartureQuery(routeId, userId, dayOfWeek));
            if (optimal is null) return Results.NoContent();

            string ics = DepartureCalendar.Build(routeId, route.DestinationAddress, optimal);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(ics), "text/calendar", "departure.ics");
        });

        return routes;
    }
}
