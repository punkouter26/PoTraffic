using MediatR;
using Microsoft.AspNetCore.Mvc;
using PoTraffic.Api.Infrastructure.Security;

namespace PoTraffic.Api.Features.History;

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
            Guid routeId,
            ISender sender,
            HttpContext ctx,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] DateTime? sinceUtc = null) =>
        {
            Guid userId = ctx.User.GetUserId();
            var result = await sender.Send(
                new GetPollHistoryQuery(routeId, userId, page, pageSize, sinceUtc));
            return Results.Ok(result);
        });

        // GET /api/routes/{routeId}/baseline?dayOfWeek=Monday
        group.MapGet("/baseline", async (
            Guid routeId,
            ISender sender,
            HttpContext ctx,
            [FromQuery] string dayOfWeek = "Monday") =>
        {
            Guid userId = ctx.User.GetUserId();
            var result = await sender.Send(new GetBaselineQuery(routeId, userId, dayOfWeek));
            return Results.Ok(result);
        });

        // GET /api/routes/{routeId}/sessions
        group.MapGet("/sessions", async (
            Guid routeId,
            ISender sender,
            HttpContext ctx) =>
        {
            Guid userId = ctx.User.GetUserId();
            var result = await sender.Send(new GetSessionsQuery(routeId, userId));
            return Results.Ok(result);
        });

        // GET /api/routes/{routeId}/optimal-departure?dayOfWeek=Monday
        group.MapGet("/optimal-departure", async (
            Guid routeId,
            ISender sender,
            HttpContext ctx,
            [FromQuery] string dayOfWeek = "Monday") =>
        {
            Guid userId = ctx.User.GetUserId();
            var result = await sender.Send(new GetOptimalDepartureQuery(routeId, userId, dayOfWeek));
            return result is null ? Results.NoContent() : Results.Ok(result);
        });

        return routes;
    }
}
