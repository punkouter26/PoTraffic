using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PoTraffic.API.Features.Account;
using PoTraffic.API.Infrastructure.Security;
using PoTraffic.Shared.DTOs.Account;

namespace PoTraffic.API.Features.Account;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder grp = app.MapGroup("/api/account")
            .RequireAuthorization("ProductionMicrosoftAuth")
            .WithTags("Account");

        grp.MapGet("/profile", async (ClaimsPrincipal user, GetProfileHandler handler, CancellationToken ct) =>
        {
            UserId userId = user.GetUserId();
            ProfileDto? profile = await handler.Handle(new GetProfileQuery(userId), ct);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        })
        .WithName("GetProfile")
        .Produces<ProfileDto>()
        .Produces(StatusCodes.Status404NotFound);

        grp.MapPut("/profile", async (
            ClaimsPrincipal user,
            [FromBody] UpdateProfileRequest body,
            UpdateProfileHandler handler,
            CancellationToken ct) =>
        {
            UserId userId = user.GetUserId();
            ProfileDto? updated = await handler.Handle(new UpdateProfileCommand(userId, body.Locale), ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        })
        .WithName("UpdateProfile")
        .Produces<ProfileDto>()
        .Produces(StatusCodes.Status404NotFound);

        grp.MapPut("/timezone", async (
            ClaimsPrincipal user,
            [FromBody] SetTimeZoneRequest body,
            SetTimeZoneHandler handler,
            CancellationToken ct) =>
        {
            bool found = await handler.Handle(new SetTimeZoneCommand(user.GetUserId(), body.TimeZoneId), ct);
            return found ? Results.NoContent() : Results.NotFound();
        })
        .WithName("SetTimeZone")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

        grp.MapGet("/quota",async (ClaimsPrincipal user, GetQuotaHandler handler, CancellationToken ct) =>
        {
            UserId userId = user.GetUserId();
            QuotaDto? quota = await handler.Handle(new GetQuotaQuery(userId), ct);
            return quota is null ? Results.NotFound() : Results.Ok(quota);
        })
        .WithName("GetQuota")
        .Produces<QuotaDto>()
        .Produces(StatusCodes.Status404NotFound);

        // GDPR Art. 20 — download everything stored about the caller as one JSON file
        grp.MapGet("/export", async (ClaimsPrincipal user, ExportAccountHandler handler, CancellationToken ct) =>
        {
            AccountExport? export = await handler.Handle(new ExportAccountQuery(user.GetUserId()), ct);
            return export is null
                ? Results.NotFound()
                : Results.File(
                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(export, ExportJson),
                    "application/json",
                    $"potraffic-export-{DateTime.UtcNow:yyyy-MM-dd}.json");
        })
        .WithName("ExportAccount");

        // FR-031: GDPR Art. 17 — self-service account deletion
        grp.MapDelete("/", async (ClaimsPrincipal user, DeleteAccountCommandHandler handler, CancellationToken ct) =>
        {
            UserId userId = user.GetUserId();
            bool deleted = await handler.Handle(new DeleteAccountCommand(userId), ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteAccount")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static readonly System.Text.Json.JsonSerializerOptions ExportJson =
        new(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };

}
