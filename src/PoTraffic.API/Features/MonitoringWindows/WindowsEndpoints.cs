
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using PoTraffic.API.Infrastructure.Security;
using PoTraffic.Shared.DTOs.Routes;

namespace PoTraffic.API.Features.MonitoringWindows;

public static class WindowsEndpoints
{
    public static IEndpointRouteBuilder MapWindowsEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/routes/{routeId:guid}/windows")
            .RequireAuthorization("ProductionMicrosoftAuth")
            .WithTags("Windows");

        group.MapGet("", GetWindows);
        group.MapPost("", CreateWindow);
        group.MapPut("{windowId:guid}", UpdateWindow);
        group.MapDelete("{windowId:guid}", DeleteWindow);
        group.MapPost("{windowId:guid}/start", StartWindow);
        group.MapPost("{windowId:guid}/stop", StopWindow);

        return app;
    }

    private static UserId? ExtractUserId(ClaimsPrincipal user)
        => user.GetUserIdOrNull();

    // GET /api/routes/{routeId}/windows — direct query on MonitoringWindows table
    private static async Task<IResult> GetWindows(
        RouteId routeId,
        HttpContext context,
        ISender sender)
    {
        UserId? userId = ExtractUserId(context.User);
        if (userId is null) return Results.Unauthorized();

        IReadOnlyList<MonitoringWindowDto>? windows =
            await sender.Send(new GetWindowsQuery(routeId, userId.Value));

        return windows is null
            ? Results.NotFound()
            : Results.Ok(windows);
    }

    // POST /api/routes/{routeId}/windows
    private static async Task<IResult> CreateWindow(
        RouteId routeId,
        HttpContext context,
        ISender sender,
        [FromBody] CreateWindowRequest request)
    {
        UserId? userId = ExtractUserId(context.User);
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
                "NOT_FOUND" => Results.NotFound(),
                "WINDOW_ALREADY_ACTIVE" => Results.Conflict(new { error = result.ErrorCode }),
                _ => Results.UnprocessableEntity(new { error = result.ErrorCode })
            };
    }

    // PUT /api/routes/{routeId}/windows/{windowId} — stub
    private static IResult UpdateWindow(RouteId routeId, WindowId windowId) =>
        Results.StatusCode(501); // Not Implemented — Phase 4

    // DELETE /api/routes/{routeId}/windows/{windowId}
    private static async Task<IResult> DeleteWindow(
        RouteId routeId,
        WindowId windowId,
        HttpContext context,
        ISender sender)
    {
        UserId? userId = ExtractUserId(context.User);
        if (userId is null) return Results.Unauthorized();

        bool deleted = await sender.Send(new DeleteWindowCommand(windowId, userId.Value));
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    // POST /api/routes/{routeId}/windows/{windowId}/start
    private static async Task<IResult> StartWindow(
        RouteId routeId,
        WindowId windowId,
        HttpContext context,
        ISender sender)
    {
        UserId? userId = ExtractUserId(context.User);
        if (userId is null) return Results.Unauthorized();

        StartWindowResult result = await sender.Send(new StartWindowCommand(windowId, userId.Value));

        if (!result.IsSuccess)
        {
            return result.ErrorCode switch
            {
                "NOT_FOUND" => Results.NotFound(),
                "QUOTA_EXCEEDED" => Results.StatusCode(429),
                _ => Results.UnprocessableEntity(new { error = result.ErrorCode })
            };
        }

        return Results.Ok(new
        {
            sessionId = result.SessionId,
            quotaRemaining = result.QuotaRemaining
        });
    }

    // POST /api/routes/{routeId}/windows/{windowId}/stop
    private static async Task<IResult> StopWindow(
        RouteId routeId,
        WindowId windowId,
        HttpContext context,
        ISender sender,
        [FromBody] StopWindowRequest request)
    {
        UserId? userId = ExtractUserId(context.User);
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

public sealed record StopWindowRequest(SessionId SessionId);
