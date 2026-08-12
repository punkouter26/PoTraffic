using Microsoft.AspNetCore.Mvc;
using PoTraffic.API.Features.Routes;
using PoTraffic.API.Infrastructure.Http;
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
            return ConditionalJson.Ok(ctx, result);
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
            return ConditionalJson.Ok(ctx, result);
        });

        // GET /api/routes/{routeId}/sessions
        group.MapGet("/sessions", async (
            RouteId routeId,
            ISender sender,
            HttpContext ctx) =>
        {
            UserId userId = ctx.User.GetUserId();
            var result = await sender.Send(new GetSessionsQuery(routeId, userId));
            return ConditionalJson.Ok(ctx, result);
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
            return result is null ? Results.NoContent() : ConditionalJson.Ok(ctx, result);
        });

        // The weekday-comparison endpoint was removed with the bar chart it fed. It and
        // the heatmap were two renderings of the same aggregate, and the grid says
        // everything the bars said, per hour rather than per day.

        // GET /api/routes/{routeId}/heatmap — day-of-week × hour congestion grid (#5)
        group.MapGet("/heatmap", async (
            RouteId routeId,
            ISender sender,
            HttpContext ctx) =>
        {
            UserId userId = ctx.User.GetUserId();
            var result = await sender.Send(new GetVolatilityHeatmapQuery(routeId, userId));
            return ConditionalJson.Ok(ctx, result);
        });

        // GET /api/routes/{routeId} — single route (drives the return-trip link, #3)
        group.MapGet("", async (
            RouteId routeId,
            ISender sender,
            HttpContext ctx) =>
        {
            UserId userId = ctx.User.GetUserId();
            RouteDto? route = await sender.Send(new GetRouteByIdQuery(routeId, userId));
            return route is null ? Results.NotFound() : ConditionalJson.Ok(ctx, route);
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
