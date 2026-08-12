using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using PoTraffic.API.Infrastructure.Security;
using PoTraffic.Shared.DTOs.Routes;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Routes;

/// <summary>Request DTO for the standalone verify-address endpoint.</summary>
public sealed record VerifySingleAddressRequest(string Address, int Provider);

public static class RoutesEndpoints
{
    private sealed class LogCategory;

    public static IEndpointRouteBuilder MapRoutesEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/routes")
            .RequireAuthorization("ProductionMicrosoftAuth")
            .WithTags("Routes");

        group.MapGet("", GetRoutes);
        group.MapPost("", CreateRoute);
        // Modernization: Dynamic provider discovery for UI
        group.MapGet("providers", GetProviders);
        // Standalone verify — no route ID required (used by create-route form)
        group.MapPost("verify-address", VerifySingleAddress);
        group.MapPut("{routeId:guid}", UpdateRoute);
        group.MapDelete("{routeId:guid}", DeleteRoute);
        group.MapPost("{routeId:guid}/check-now", CheckNow);
        group.MapPost("{routeId:guid}/return-trip", CreateReturnTrip);
        // Sample route with synthetic history, for an account that has nothing to show yet (#10)
        return app;
    }

    private static IResult GetProviders()
    {
        // Strategy discovery — list all available RouteProvider enum values
        var list = Enum.GetValues<RouteProvider>()
            .Select(p => new { value = (int)p, label = p.ToString() });
        return Results.Ok(list);
    }

    private static UserId? ExtractUserId(ClaimsPrincipal user, ILogger? logger = null)
        => user.GetUserIdOrNull(logger);

    // POST /api/routes/verify-address
    private static async Task<IResult> VerifySingleAddress(
        ISender sender,
        [FromBody] VerifySingleAddressRequest request)
    {
        VerifySingleAddressResult result = await sender.Send(
            new VerifySingleAddressCommand(request.Address, (RouteProvider)request.Provider));

        return result.IsValid
            ? Results.Ok(new { isValid = true, coordinates = result.Coordinates })
            : Results.UnprocessableEntity(new { isValid = false, errorCode = result.ErrorCode });
    }

    // GET /api/routes?page=1&pageSize=20
    private static async Task<IResult> GetRoutes(
        HttpContext context,
        ISender sender,
        ILogger<LogCategory> logger,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        UserId? userId = ExtractUserId(context.User, logger);
        if (userId is null) return Results.Unauthorized();

        PagedResult<RouteDto> result = await sender.Send(
            new GetRoutesQuery(userId.Value, page, pageSize));

        return Results.Ok(result);
    }

    // POST /api/routes
    private static async Task<IResult> CreateRoute(
        HttpContext context,
        ISender sender,
        ILogger<LogCategory> logger,
        [FromBody] CreateRouteRequest request)
    {
        UserId? userId = ExtractUserId(context.User, logger);
        if (userId is null) return Results.Unauthorized();

        CreateRouteResult result = await sender.Send(
            new CreateRouteCommand(
                userId.Value,
                request.OriginAddress,
                request.DestinationAddress,
                request.Provider,
                request.StartTime,
                request.EndTime,
                request.DaysOfWeekMask));

        return result.IsSuccess
            ? Results.Created($"/api/routes/{result.Route!.Id}", result.Route)
            : Results.UnprocessableEntity(new { error = result.ErrorCode });
    }

    // PUT /api/routes/{routeId}
    private static async Task<IResult> UpdateRoute(
        RouteId routeId,
        HttpContext context,
        ISender sender,
        [FromBody] UpdateRouteRequest request)
    {
        UserId? userId = ExtractUserId(context.User);
        if (userId is null) return Results.Unauthorized();

        UpdateRouteResult result = await sender.Send(
            new UpdateRouteCommand(routeId, userId.Value, request.OriginAddress, request.DestinationAddress, request.Provider));

        return result.IsSuccess
            ? Results.Ok(result.Route)
            : result.ErrorCode == RouteErrorCodes.NotFound
                ? Results.NotFound()
                : Results.UnprocessableEntity(new { error = result.ErrorCode });
    }

    // DELETE /api/routes/{routeId}
    private static async Task<IResult> DeleteRoute(
        RouteId routeId,
        HttpContext context,
        ISender sender)
    {
        UserId? userId = ExtractUserId(context.User);
        if (userId is null) return Results.Unauthorized();

        bool deleted = await sender.Send(new DeleteRouteCommand(routeId, userId.Value));
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    // POST /api/routes/{routeId}/return-trip — create + link the reverse-direction route (#3)
    private static async Task<IResult> CreateReturnTrip(
        RouteId routeId,
        HttpContext context,
        ISender sender,
        ILogger<LogCategory> logger)
    {
        UserId? userId = ExtractUserId(context.User, logger);
        if (userId is null) return Results.Unauthorized();

        CreateRouteResult result = await sender.Send(new CreateReturnTripCommand(routeId, userId.Value));
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
        ISender sender,
        ILogger<LogCategory> logger)
    {
        UserId? userId = ExtractUserId(context.User, logger);
        if (userId is null) return Results.Unauthorized();

        CheckNowResult result = await sender.Send(new CheckNowCommand(routeId, userId.Value));

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

// Request DTO scoped to this endpoint only
public sealed record UpdateRouteRequest(
    string? OriginAddress,
    string? DestinationAddress,
    RouteProvider? Provider);
