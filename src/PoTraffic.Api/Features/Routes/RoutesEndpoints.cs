
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Shared.DTOs.Routes;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Routes;

/// <summary>Request DTO for the standalone verify-address endpoint.</summary>
public sealed record VerifySingleAddressRequest(string Address, int Provider);

public static class RoutesEndpoints
{
    private sealed class LogCategory;

    public static IEndpointRouteBuilder MapRoutesEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/routes")
            .RequireAuthorization()
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

        return app;
    }

    private static IResult GetProviders()
    {
        // Strategy discovery — list all available RouteProvider enum values
        var list = Enum.GetValues<RouteProvider>()
            .Select(p => new { value = (int)p, label = p.ToString() });
        return Results.Ok(list);
    }

    private static Guid? ExtractUserId(ClaimsPrincipal user, ILogger? logger = null)
    {
        // T112: Robust user ID extraction for both OIDC (sub) and ASP.NET Identity (NameIdentifier)
        // Check for NameIdentifier first as it's the standard .NET mapping
        string? raw = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? 
                      user.FindFirstValue("sub") ??
                      user.Identity?.Name; // Fallback to Identity.Name which we set to "sub" in Program.cs

        if (Guid.TryParse(raw, out Guid id))
        {
            // Debug log to confirm identity hydration in E2E/Production
            // logger?.LogDebug("[Auth] ExtractUserId: Found {UserId}", id);
            return id;
        }

        logger?.LogWarning("[Auth] ExtractUserId: FAILED. Raw: '{Raw}', Claims: {Claims}", 
            raw ?? "NULL",
            string.Join(", ", user.Claims.Take(5).Select(c => $"{c.Type}={c.Value}")));
        
        return null;
    }

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
        Guid? userId = ExtractUserId(context.User, logger);
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
        Guid? userId = ExtractUserId(context.User, logger);
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
            : result.ErrorCode switch
            {
                "GEOCODE_FAILED"     => Results.UnprocessableEntity(new { error = result.ErrorCode }),
                "SAME_COORDINATES"   => Results.UnprocessableEntity(new { error = result.ErrorCode }),
                _                    => Results.UnprocessableEntity(new { error = result.ErrorCode })
            };
    }

    // PUT /api/routes/{routeId}
    private static async Task<IResult> UpdateRoute(
        Guid routeId,
        HttpContext context,
        ISender sender,
        [FromBody] UpdateRouteRequest request)
    {
        Guid? userId = ExtractUserId(context.User);
        if (userId is null) return Results.Unauthorized();

        UpdateRouteResult result = await sender.Send(
            new UpdateRouteCommand(routeId, userId.Value, request.OriginAddress, request.DestinationAddress, request.Provider));

        return result.IsSuccess
            ? Results.Ok(result.Route)
            : result.ErrorCode switch
            {
                "NOT_FOUND"          => Results.NotFound(),
                "GEOCODE_FAILED"     => Results.UnprocessableEntity(new { error = result.ErrorCode }),
                "SAME_COORDINATES"   => Results.UnprocessableEntity(new { error = result.ErrorCode }),
                _                    => Results.UnprocessableEntity(new { error = result.ErrorCode })
            };
    }

    // DELETE /api/routes/{routeId}
    private static async Task<IResult> DeleteRoute(
        Guid routeId,
        HttpContext context,
        ISender sender)
    {
        Guid? userId = ExtractUserId(context.User);
        if (userId is null) return Results.Unauthorized();

        bool deleted = await sender.Send(new DeleteRouteCommand(routeId, userId.Value));
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    // POST /api/routes/{routeId}/check-now — instant poll, result not persisted
    private static async Task<IResult> CheckNow(
        Guid routeId,
        HttpContext context,
        ITrafficProviderFactory providerFactory,
        ISender sender,
        ILogger<LogCategory> logger)
    {
        Guid? userId = ExtractUserId(context.User, logger);
        if (userId is null) return Results.Unauthorized();

        // Verify ownership via route query (fetch single route)
        PagedResult<RouteDto> routes = await sender.Send(new GetRoutesQuery(userId.Value, 1, 1000));
        RouteDto? route = routes.Items.FirstOrDefault(r => r.Id == routeId);
        if (route is null) return Results.NotFound();

        // Resolve provider and get live travel time without persisting
        ITrafficProvider provider = providerFactory.GetProvider(route.Provider);
        TravelResult? travelResult = await provider.GetTravelTimeAsync(
            route.OriginCoordinates, route.DestinationCoordinates);

        return travelResult is null
            ? Results.StatusCode(503)
            : Results.Ok(new
            {
                durationSeconds = travelResult.DurationSeconds,
                distanceMetres  = travelResult.DistanceMetres
            });
    }

}

// Request DTO scoped to this endpoint only
public sealed record UpdateRouteRequest(
    string? OriginAddress,
    string? DestinationAddress,
    RouteProvider? Provider);
