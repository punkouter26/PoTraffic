using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using PoTraffic.API.Infrastructure.Security;
using PoTraffic.Shared.DTOs.Routes;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Routes;

/// <summary>
/// Request DTO for the sample-route endpoint. The offset is the browser's, so the generated
/// commute lands on the user's morning; a missing body defaults it to UTC.
/// </summary>
public sealed record CreateSampleRouteRequest(int UtcOffsetMinutes);

public static class RoutesEndpoints
{
    private sealed class LogCategory;

    public static IEndpointRouteBuilder MapRoutesEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/routes")
            .RequireAuthorization("ProductionMicrosoftAuth")
            .WithTags("Routes");

        group.MapGet("", GetRoutes);
        // Each of these spends paid provider calls (geocoding / directions).
        group.MapPost("", CreateRoute).RequireRateLimiting(PoTraffic.API.Infrastructure.Security.RateLimitingExtensions.ProviderCalls);
        // Sample route with synthetic history, for an account that has nothing to show yet (#10)
        group.MapPost("sample", CreateSampleRoute);
        group.MapDelete("{routeId:guid}", DeleteRoute);
        group.MapPost("{routeId:guid}/check-now", CheckNow).RequireRateLimiting(PoTraffic.API.Infrastructure.Security.RateLimitingExtensions.ProviderCalls);
        group.MapPost("{routeId:guid}/return-trip", CreateReturnTrip).RequireRateLimiting(PoTraffic.API.Infrastructure.Security.RateLimitingExtensions.ProviderCalls);
        // Road shape + how today compares, for the map on the route detail page.
        group.MapGet("{routeId:guid}/path", GetRoutePath);
        // Arrive-by target behind the departure plan; null body value clears it.
        group.MapPut("{routeId:guid}/arrive-by", async (
            RouteId routeId, HttpContext ctx, SetArriveByHandler handler, [FromBody] SetArriveByRequest body) =>
        {
            bool found = await handler.Handle(new SetArriveByCommand(routeId, ctx.User.GetUserId(), body.ArriveBy), CancellationToken.None);
            return found ? Results.NoContent() : Results.NotFound();
        });
        group.MapPut("{routeId:guid}/name", async (
            RouteId routeId, HttpContext ctx, SetRouteNameHandler handler, [FromBody] SetRouteNameRequest body) =>
        {
            bool found = await handler.Handle(new SetRouteNameCommand(routeId, ctx.User.GetUserId(), body.Name), CancellationToken.None);
            return found ? Results.NoContent() : Results.NotFound();
        });
        return app;
    }

    private static UserId? ExtractUserId(ClaimsPrincipal user, ILogger? logger = null)
        => user.GetUserIdOrNull(logger);

    // POST /api/routes/sample
    private static async Task<IResult> CreateSampleRoute(
        HttpContext context,
        CreateSampleRouteCommandHandler handler,
        ILogger<LogCategory> logger,
        [FromBody] CreateSampleRouteRequest? request = null)
    {
        UserId? userId = ExtractUserId(context.User, logger);
        if (userId is null) return Results.Unauthorized();

        RouteDto route = await handler.Handle(
            new CreateSampleRouteCommand(userId.Value, request?.UtcOffsetMinutes ?? 0), CancellationToken.None);

        return Results.Ok(route);
    }

    // GET /api/routes?page=1&pageSize=20
    private static async Task<IResult> GetRoutes(
        HttpContext context,
        GetRoutesQueryHandler handler,
        ILogger<LogCategory> logger,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        UserId? userId = ExtractUserId(context.User, logger);
        if (userId is null) return Results.Unauthorized();

        PagedResult<RouteDto> result = await handler.Handle(
            new GetRoutesQuery(userId.Value, page, pageSize), context.RequestAborted);

        return Results.Ok(result);
    }

    // POST /api/routes
    private static async Task<IResult> CreateRoute(
        HttpContext context,
        CreateRouteCommandHandler handler,
        ILogger<LogCategory> logger,
        [FromBody] CreateRouteRequest request)
    {
        UserId? userId = ExtractUserId(context.User, logger);
        if (userId is null) return Results.Unauthorized();

        CreateRouteResult result = await handler.Handle(
            new CreateRouteCommand(
                userId.Value,
                request.OriginAddress,
                request.DestinationAddress,
                request.Provider,
                request.StartTime,
                request.EndTime,
                request.DaysOfWeekMask,
                request.TimeZoneId), CancellationToken.None);

        return result.IsSuccess
            ? Results.Created($"/api/routes/{result.Route!.Id}", result.Route)
            : Results.UnprocessableEntity(new { error = result.ErrorCode });
    }

    // GET /api/routes/{routeId}/path — road shape + traffic tint for the map
    private static async Task<IResult> GetRoutePath(
        RouteId routeId,
        HttpContext context,
        GetRoutePathQueryHandler handler,
        ILogger<LogCategory> logger)
    {
        UserId? userId = ExtractUserId(context.User, logger);
        if (userId is null) return Results.Unauthorized();

        RoutePathDto? path = await handler.Handle(new GetRoutePathQuery(routeId, userId.Value), context.RequestAborted);
        return path is null ? Results.NotFound() : Results.Ok(path);
    }

    // DELETE /api/routes/{routeId}
    private static async Task<IResult> DeleteRoute(
        RouteId routeId,
        HttpContext context,
        DeleteRouteCommandHandler handler)
    {
        UserId? userId = ExtractUserId(context.User);
        if (userId is null) return Results.Unauthorized();

        bool deleted = await handler.Handle(new DeleteRouteCommand(routeId, userId.Value), CancellationToken.None);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    // POST /api/routes/{routeId}/return-trip — create + link the reverse-direction route (#3)
    private static async Task<IResult> CreateReturnTrip(
        RouteId routeId,
        HttpContext context,
        CreateReturnTripCommandHandler handler,
        ILogger<LogCategory> logger)
    {
        UserId? userId = ExtractUserId(context.User, logger);
        if (userId is null) return Results.Unauthorized();

        CreateRouteResult result = await handler.Handle(new CreateReturnTripCommand(routeId, userId.Value), CancellationToken.None);
        return result.IsSuccess
            ? Results.Created($"/api/routes/{result.Route!.Id}", result.Route)
            : result.ErrorCode == RouteErrorCodes.NotFound
                ? Results.NotFound()
                : Results.UnprocessableEntity(new { error = result.ErrorCode });
    }

    // POST /api/routes/{routeId}/check-now — instant poll, result not persisted
    private static async Task<IResult> CheckNow(
        RouteId routeId,
        HttpContext context,
        CheckNowCommandHandler handler,
        ILogger<LogCategory> logger)
    {
        UserId? userId = ExtractUserId(context.User, logger);
        if (userId is null) return Results.Unauthorized();

        CheckNowResult result = await handler.Handle(new CheckNowCommand(routeId, userId.Value), CancellationToken.None);

        if (result.IsSuccess)
            return Results.Ok(new
            {
                durationSeconds = result.DurationSeconds,
                distanceMetres = result.DistanceMetres
            });

        return result.ErrorCode switch
        {
            RouteErrorCodes.NotFound => Results.NotFound(),
            _ => Results.StatusCode(503)
        };
    }

}

// UpdateRouteRequest was removed (#1 cleanup): no client caller; the editing surface (origin/destination/provider)
// is reached via DELETE + re-create, which is the documented user flow.
