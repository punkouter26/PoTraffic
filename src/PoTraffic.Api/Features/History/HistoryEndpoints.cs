using MediatR;
using Microsoft.AspNetCore.Mvc;

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
            .RequireAuthorization()
            .WithTags("History");

        // GET /api/routes/{routeId}/poll-history?page=1&pageSize=20
        group.MapGet("/poll-history", async (
            Guid routeId,
            ISender sender,
            HttpContext ctx,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50) =>
        {
            Guid userId = ctx.GetUserId();
            var result = await sender.Send(
                new GetPollHistoryQuery(routeId, userId, page, pageSize));
            return Results.Ok(result);
        });

        // GET /api/routes/{routeId}/baseline?dayOfWeek=Monday
        group.MapGet("/baseline", async (
            Guid routeId,
            ISender sender,
            [FromQuery] string dayOfWeek = "Monday") =>
        {
            var result = await sender.Send(new GetBaselineQuery(routeId, dayOfWeek));
            return Results.Ok(result);
        });

        // GET /api/routes/{routeId}/sessions
        group.MapGet("/sessions", async (
            Guid routeId,
            ISender sender,
            HttpContext ctx) =>
        {
            Guid userId = ctx.GetUserId();
            var result = await sender.Send(new GetSessionsQuery(routeId, userId));
            return Results.Ok(result);
        });

        // GET /api/routes/{routeId}/optimal-departure?dayOfWeek=Monday
        group.MapGet("/optimal-departure", async (
            Guid routeId,
            ISender sender,
            [FromQuery] string dayOfWeek = "Monday") =>
        {
            var result = await sender.Send(new GetOptimalDepartureQuery(routeId, dayOfWeek));
            return result is null ? Results.NoContent() : Results.Ok(result);
        });

        return routes;
    }
}

/// <summary>
/// Extension to extract the authenticated user's ID from the JWT claim.
/// </summary>
file static class HttpContextExtensions
{
    internal static Guid GetUserId(this HttpContext ctx)
    {
        string? sub = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                   ?? ctx.User.FindFirst("sub")?.Value;
        return sub is not null && Guid.TryParse(sub, out Guid id) ? id : Guid.Empty;
    }
}
