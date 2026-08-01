using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoTraffic.API.Features.Admin;
using PoTraffic.API.Infrastructure;
using PoTraffic.API.Infrastructure.Scheduling;
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
            IReadOnlyList<GlobalVolatilitySlotDto> points = await sender.Send(new GetRecentVolatilityQuery(hours), ct);
            return Results.Ok(points);
        })
        .WithName("GetRecentVolatility")
        .Produces<IReadOnlyList<GlobalVolatilitySlotDto>>();

        grp.MapGet("/poll-cost-summary", async (ISender sender, CancellationToken ct) =>
        {
            IReadOnlyList<PollCostSummaryDto> summary = await sender.Send(new GetPollCostSummaryQuery(), ct);
            return Results.Ok(summary);
        })
        .WithName("GetPollCostSummary")
        .Produces<IReadOnlyList<PollCostSummaryDto>>();

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

        // Triple Test endpoints — FR-TT: admin-only diagnostic travel-time sampler
        grp.MapPost("/triple-test", async (
            [FromBody] TripleTestRequest body,
            ISender sender,
            CancellationToken ct) =>
        {
            StartTripleTestResult result = await sender.Send(
                new StartTripleTestCommand(
                    body.OriginAddress,
                    body.DestinationAddress,
                    body.Provider,
                    body.StartAt), ct);

            return result.IsSuccess
                ? Results.Accepted($"/api/admin/triple-test/{result.SessionId}", new { sessionId = result.SessionId })
                : Results.BadRequest(new { error = result.ErrorCode });
        })
        .WithName("StartTripleTest")
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest);

        grp.MapGet("/triple-test/{sessionId:guid}", async (
            TripleTestSessionId sessionId,
            ISender sender,
            CancellationToken ct) =>
        {
            TripleTestSessionDto? dto = await sender.Send(new GetTripleTestSessionQuery(sessionId), ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        })
        .WithName("GetTripleTestSession")
        .Produces<TripleTestSessionDto>()
        .Produces(StatusCodes.Status404NotFound);

        // US-ADM: POST /api/admin/seed-data — populates demo data for demo/demo flow
        grp.MapPost("/seed-data", async (
            [FromBody] SeedRequest request,
            ISender sender,
            CancellationToken ct) =>
        {
            SeedDatabaseResult result = await sender.Send(new SeedDatabaseCommand(request.RouteCount, request.DaysOfHistory), ct);
            return Results.Ok(result);
        })
        .WithName("SeedDatabase")
        .Produces<SeedDatabaseResult>();

        // US-ADM: POST /api/admin/clear-data — purges demo/volatile data
        grp.MapPost("/clear-data", async (
            ISender sender,
            CancellationToken ct) =>
        {
            ClearDatabaseResult result = await sender.Send(new ClearDatabaseCommand(), ct);
            return Results.Ok(result);
        })
        .WithName("ClearDatabase")
        .Produces<ClearDatabaseResult>();

        // GET /api/admin/connection-health — live ping status for all external dependencies.
        // Shares ConnectionHealthReporter with the anonymous /health/connections feed the
        // Blazor health page reads, so the two can never report different things.
        grp.MapGet("/connection-health", async (
            HealthCheckService healthCheckService,
            IConfiguration configuration,
            CancellationToken ct) =>
            Results.Ok(await ConnectionHealthReporter.BuildAsync(healthCheckService, configuration, ct)))
        .WithName("GetConnectionHealth")
        .Produces<IEnumerable<ConnectionHealthDto>>();

        return app;
    }

    private sealed record SeedRequest(int RouteCount = 3, int DaysOfHistory = 14);
}

// Request DTO scoped to this file
internal sealed record UpdateConfigRequest(string Value);
