using Microsoft.AspNetCore.Mvc;
using PoTraffic.API.Features.Routes;
using PoTraffic.API.Infrastructure.Http;
using PoTraffic.API.Infrastructure.Security;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.API.Infrastructure.Time;
using PoTraffic.Shared.DTOs.History;
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
            GetPollHistoryQueryHandler handler,
            HttpContext ctx,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] DateTime? sinceUtc = null) =>
        {
            UserId userId = ctx.User.GetUserId();
            var result = await handler.Handle(
                new GetPollHistoryQuery(routeId, userId, page, pageSize, sinceUtc), ctx.RequestAborted);
            return ConditionalJson.Ok(ctx, result);
        });

        // GET /api/routes/{routeId}/sessions
        group.MapGet("/sessions", async (
            RouteId routeId,
            GetSessionsQueryHandler handler,
            HttpContext ctx) =>
        {
            UserId userId = ctx.User.GetUserId();
            var result = await handler.Handle(new GetSessionsQuery(routeId, userId), ctx.RequestAborted);
            return ConditionalJson.Ok(ctx, result);
        });

        // GET /api/routes/{routeId}/optimal-departure?dayOfWeek=Monday
        group.MapGet("/optimal-departure", async (
            RouteId routeId,
            GetOptimalDepartureQueryHandler handler,
            HttpContext ctx,
            [FromQuery] string dayOfWeek = "Monday") =>
        {
            UserId userId = ctx.User.GetUserId();
            var result = await handler.Handle(new GetOptimalDepartureQuery(routeId, userId, dayOfWeek), ctx.RequestAborted);
            return result is null ? Results.NoContent() : ConditionalJson.Ok(ctx, result);
        });

        // GET /api/routes/{routeId}/departure-plan?dayOfWeek=Monday — leave-by for the arrive-by target
        group.MapGet("/departure-plan", async (
            RouteId routeId,
            GetDeparturePlanQueryHandler handler,
            HttpContext ctx,
            [FromQuery] string dayOfWeek = "Monday") =>
        {
            UserId userId = ctx.User.GetUserId();
            DeparturePlanDto? plan = await handler.Handle(new GetDeparturePlanQuery(routeId, userId, dayOfWeek), ctx.RequestAborted);
            return plan is null ? Results.NoContent() : ConditionalJson.Ok(ctx, plan);
        });

        // The weekday-comparison endpoint was removed with the bar chart it fed. It and
        // the heatmap were two renderings of the same aggregate, and the grid says
        // everything the bars said, per hour rather than per day.

        // GET /api/routes/{routeId}/heatmap — day-of-week × hour congestion grid (#5)
        group.MapGet("/heatmap", async (
            RouteId routeId,
            GetVolatilityHeatmapQueryHandler handler,
            HttpContext ctx) =>
        {
            UserId userId = ctx.User.GetUserId();
            var result = await handler.Handle(new GetVolatilityHeatmapQuery(routeId, userId), ctx.RequestAborted);
            return ConditionalJson.Ok(ctx, result);
        });

        // GET /api/routes/{routeId}/weather-impact — what each condition costs this route
        group.MapGet("/weather-impact", async (
            RouteId routeId,
            GetWeatherImpactQueryHandler handler,
            HttpContext ctx) =>
        {
            UserId userId = ctx.User.GetUserId();
            var result = await handler.Handle(new GetWeatherImpactQuery(routeId, userId), ctx.RequestAborted);
            return ConditionalJson.Ok(ctx, result);
        });

        // GET /api/routes/{routeId} — single route (drives the return-trip link, #3)
        group.MapGet("", async (
            RouteId routeId,
            GetRouteByIdQueryHandler handler,
            HttpContext ctx) =>
        {
            UserId userId = ctx.User.GetUserId();
            RouteDto? route = await handler.Handle(new GetRouteByIdQuery(routeId, userId), ctx.RequestAborted);
            return route is null ? Results.NotFound() : ConditionalJson.Ok(ctx, route);
        });

        // GET /api/routes/{routeId}/departure.ics?dayOfWeek=Monday — calendar reminder (#2)
        group.MapGet("/departure.ics", async (
            RouteId routeId,
            GetRouteByIdQueryHandler routes,
            GetOptimalDepartureQueryHandler optimalDeparture,
            GetDeparturePlanQueryHandler departurePlan,
            TableStorageContext db,
            HttpContext ctx,
            [FromQuery] string dayOfWeek = "Monday") =>
        {
            UserId userId = ctx.User.GetUserId();
            RouteDto? route = await routes.Handle(new GetRouteByIdQuery(routeId, userId), ctx.RequestAborted);
            if (route is null) return Results.NotFound();

            var optimal = await optimalDeparture.Handle(new GetOptimalDepartureQuery(routeId, userId, dayOfWeek), ctx.RequestAborted);
            if (optimal is null) return Results.NoContent();

            // With an arrive-by target the reminder is the planned leave-by, not the
            // fastest slot: that is the time the user actually acts on.
            DeparturePlanDto? plan = await departurePlan.Handle(new GetDeparturePlanQuery(routeId, userId, dayOfWeek), ctx.RequestAborted);
            if (plan is { LeaveBy: { } leaveBy, WorstCaseSeconds: { } worst })
            {
                TimeOnly t = TimeOnly.ParseExact(leaveBy, "HH:mm");
                optimal = optimal with { TimeSlotBucket = t.Hour * 60 + t.Minute, PredictedDurationSeconds = worst };
            }

            string ics = DepartureCalendar.Build(routeId, route.DestinationAddress, optimal, db.ZoneFor(userId));
            return Results.File(System.Text.Encoding.UTF8.GetBytes(ics), "text/calendar", "departure.ics");
        });

        return routes;
    }
}
