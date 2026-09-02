using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoTraffic.API.Features.Admin;
using PoTraffic.API.Infrastructure;
using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Admin;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // FR-022: All admin endpoints require Administrator role
        RouteGroupBuilder grp = app.MapGroup("/api/admin")
            .RequireAuthorization("AdminOnly")
            .RequireAuthorization("ProductionMicrosoftAuth")
            .WithTags("Admin");

        grp.MapGet("/users", async (ISender sender, CancellationToken ct) =>
        {
            IReadOnlyList<UserDailyUsageDto> users = await sender.Send(new GetUsersQuery(), ct);
            return Results.Ok(users);
        })
        .WithName("GetAdminUsers")
        .Produces<IReadOnlyList<UserDailyUsageDto>>();

        grp.MapGet("/configuration", async (ISender sender, CancellationToken ct) =>
        {
            IReadOnlyList<SystemConfigDto> configs = await sender.Send(new GetSystemConfigurationQuery(), ct);
            return Results.Ok(configs);
        })
        .WithName("GetSystemConfiguration")
        .Produces<IReadOnlyList<SystemConfigDto>>();

        // T120: GET /api/admin/global-volatility (US5 FR-024) — historical day-of-week baseline
        grp.MapGet("/global-volatility", async (ISender sender, CancellationToken ct) =>
        {
            IReadOnlyList<GlobalVolatilitySlotDto> slots = await sender.Send(new GetGlobalVolatilityQuery(), ct);
            return Results.Ok(slots);
        })
        .WithName("GetGlobalVolatility")
        .Produces<IReadOnlyList<GlobalVolatilitySlotDto>>();

        // GET /api/admin/global-volatility/recent?hours=24 — real-time 24h rolling time-series
        grp.MapGet("/global-volatility/recent", async (
            ISender sender,
            CancellationToken ct,
            [FromQuery] int hours = 24) =>
        {
            IReadOnlyList<RecentVolatilityPointDto> points = await sender.Send(new GetRecentVolatilityQuery(hours), ct);
            return Results.Ok(points);
        })
        .WithName("GetRecentVolatility")
        .Produces<IReadOnlyList<RecentVolatilityPointDto>>();

        grp.MapPut("/configuration/{key}", async (
            string key,
            [FromBody] UpdateConfigRequest body,
            ISender sender,
            CancellationToken ct) =>
        {
            SystemConfigDto? updated = await sender.Send(new UpdateSystemConfigurationCommand(key, body.Value), ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        })
        .WithName("UpdateSystemConfiguration")
        .Produces<SystemConfigDto>()
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}

// Request DTO scoped to this file
internal sealed record UpdateConfigRequest(string Value);
