
using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Shared.DTOs.Routes;

namespace PoTraffic.Api.Features.MonitoringWindows;

public static class WindowsEndpoints
{
    public static IEndpointRouteBuilder MapWindowsEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/routes/{routeId:guid}/windows")
            .RequireAuthorization()
            .WithTags("Windows");

        group.MapGet("", GetWindows);
        group.MapPost("", CreateWindow);
        group.MapPut("{windowId:guid}", UpdateWindow);
        group.MapDelete("{windowId:guid}", DeleteWindow);
        group.MapPost("{windowId:guid}/start", StartWindow);
        group.MapPost("{windowId:guid}/stop", StopWindow);

        return app;
    }

    private static Guid? ExtractUserId(ClaimsPrincipal user)
    {
        // T113: Ensure claim synchronization between Blazor WASM and Minimal API
        string? raw = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? 
                      user.FindFirstValue("sub") ??
                      user.Identity?.Name;
        return Guid.TryParse(raw, out Guid id) ? id : null;
    }

    // GET /api/routes/{routeId}/windows — returns the route's windows via the route DTO
    private static async Task<IResult> GetWindows(
        Guid routeId,
        HttpContext context,
        ISender sender)
    {
        Guid? userId = ExtractUserId(context.User);
        if (userId is null) return Results.Unauthorized();

        PagedResult<RouteDto> routes = await sender.Send(new GetRoutesQuery(userId.Value, 1, 1000));
        RouteDto? route = routes.Items.FirstOrDefault(r => r.Id == routeId);

        return route is null
            ? Results.NotFound()
            : Results.Ok(route.Windows);
    }

    // POST /api/routes/{routeId}/windows
    private static async Task<IResult> CreateWindow(
        Guid routeId,
        HttpContext context,
        ISender sender,
        [FromBody] CreateWindowRequest request)
    {
        Guid? userId = ExtractUserId(context.User);
        if (userId is null) return Results.Unauthorized();

        if (!TimeOnly.TryParse(request.StartTime, out TimeOnly start))
            return Results.BadRequest(new { error = "INVALID_START_TIME" });

        if (!TimeOnly.TryParse(request.EndTime, out TimeOnly end))
            return Results.BadRequest(new { error = "INVALID_END_TIME" });

        CreateWindowResult result = await sender.Send(
            new CreateWindowCommand(routeId, userId.Value, start, end, request.DaysOfWeekMask));

        return result.IsSuccess
            ? Results.Created($"/api/routes/{routeId}/windows/{result.WindowId}", new { windowId = result.WindowId })
            : result.ErrorCode switch
            {
                "NOT_FOUND"             => Results.NotFound(),
                "WINDOW_ALREADY_ACTIVE" => Results.Conflict(new { error = result.ErrorCode }),
                _                       => Results.UnprocessableEntity(new { error = result.ErrorCode })
            };
    }

    // PUT /api/routes/{routeId}/windows/{windowId} — stub
    private static IResult UpdateWindow(Guid routeId, Guid windowId) =>
        Results.StatusCode(501); // Not Implemented — Phase 4

    // DELETE /api/routes/{routeId}/windows/{windowId}
    private static async Task<IResult> DeleteWindow(
        Guid routeId,
        Guid windowId,
        HttpContext context,
        ISender sender)
    {
        Guid? userId = ExtractUserId(context.User);
        if (userId is null) return Results.Unauthorized();

        bool deleted = await sender.Send(new DeleteWindowCommand(windowId, userId.Value));
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    // POST /api/routes/{routeId}/windows/{windowId}/start
    private static async Task<IResult> StartWindow(
        Guid routeId,
        Guid windowId,
        HttpContext context,
        ISender sender)
    {
        Guid? userId = ExtractUserId(context.User);
        if (userId is null) return Results.Unauthorized();

        StartWindowResult result = await sender.Send(new StartWindowCommand(windowId, userId.Value));

        if (!result.IsSuccess)
        {
            return result.ErrorCode switch
            {
                "NOT_FOUND"     => Results.NotFound(),
                "QUOTA_EXCEEDED" => Results.StatusCode(429),
                _               => Results.UnprocessableEntity(new { error = result.ErrorCode })
            };
        }

        return Results.Ok(new
        {
            sessionId      = result.SessionId,
            quotaRemaining = result.QuotaRemaining
        });
    }

    // POST /api/routes/{routeId}/windows/{windowId}/stop
    private static async Task<IResult> StopWindow(
        Guid routeId,
        Guid windowId,
        HttpContext context,
        ISender sender,
        [FromBody] StopWindowRequest request)
    {
        Guid? userId = ExtractUserId(context.User);
        if (userId is null) return Results.Unauthorized();

        bool stopped = await sender.Send(new StopWindowCommand(request.SessionId, userId.Value));
        return stopped ? Results.NoContent() : Results.NotFound();
    }
}

// Request DTOs scoped to this endpoint only
public sealed record CreateWindowRequest(
    string StartTime,
    string EndTime,
    byte DaysOfWeekMask);

public sealed record StopWindowRequest(Guid SessionId);
