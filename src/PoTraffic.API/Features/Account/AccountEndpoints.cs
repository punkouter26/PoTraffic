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

        grp.MapGet("/profile", async (ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
        {
            UserId userId = user.GetUserId();
            ProfileDto? profile = await sender.Send(new GetProfileQuery(userId), ct);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        })
        .WithName("GetProfile")
        .Produces<ProfileDto>()
        .Produces(StatusCodes.Status404NotFound);

        grp.MapPut("/profile", async (
            ClaimsPrincipal user,
            [FromBody] UpdateProfileRequest body,
            ISender sender,
            CancellationToken ct) =>
        {
            UserId userId = user.GetUserId();
            ProfileDto? updated = await sender.Send(new UpdateProfileCommand(userId, body.Locale), ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        })
        .WithName("UpdateProfile")
        .Produces<ProfileDto>()
        .Produces(StatusCodes.Status404NotFound);

        grp.MapGet("/quota", async (ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
        {
            UserId userId = user.GetUserId();
            QuotaDto? quota = await sender.Send(new GetQuotaQuery(userId), ct);
            return quota is null ? Results.NotFound() : Results.Ok(quota);
        })
        .WithName("GetQuota")
        .Produces<QuotaDto>()
        .Produces(StatusCodes.Status404NotFound);

        // FR-031: GDPR Art. 17 — self-service account deletion
        grp.MapDelete("/", async (ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
        {
            UserId userId = user.GetUserId();
            bool deleted = await sender.Send(new DeleteAccountCommand(userId), ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteAccount")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

}
