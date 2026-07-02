using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PoTraffic.Api.Features.Account;
using PoTraffic.Api.Infrastructure.Security;
using PoTraffic.Shared.DTOs.Account;

namespace PoTraffic.Api.Features.Account;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder grp = app.MapGroup("/api/account")
            .RequireAuthorization("ProductionMicrosoftAuth")
            .WithTags("Account");

        grp.MapGet("/profile", async (ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
        {
            Guid userId = user.GetUserId();
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
            Guid userId = user.GetUserId();
            ProfileDto? updated = await sender.Send(new UpdateProfileCommand(userId, body.Locale), ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        })
        .WithName("UpdateProfile")
        .Produces<ProfileDto>()
        .Produces(StatusCodes.Status404NotFound);

        grp.MapPost("/change-password", async (
            ClaimsPrincipal user,
            [FromBody] ChangePasswordRequest body,
            ISender sender,
            CancellationToken ct) =>
        {
            Guid userId = user.GetUserId();
            ChangePasswordResult result = await sender.Send(
                new ChangePasswordCommand(userId, body.CurrentPassword, body.NewPassword, body.ConfirmNewPassword), ct);

            return result.IsSuccess
                ? Results.NoContent()
                : Results.BadRequest(new { error = result.ErrorCode });
        })
        .WithName("ChangePassword")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest);

        grp.MapGet("/quota", async (ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
        {
            Guid userId = user.GetUserId();
            QuotaDto? quota = await sender.Send(new GetQuotaQuery(userId), ct);
            return quota is null ? Results.NotFound() : Results.Ok(quota);
        })
        .WithName("GetQuota")
        .Produces<QuotaDto>()
        .Produces(StatusCodes.Status404NotFound);

        // FR-031: GDPR Art. 17 — self-service account deletion
        grp.MapDelete("/", async (ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
        {
            Guid userId = user.GetUserId();
            bool deleted = await sender.Send(new DeleteAccountCommand(userId), ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteAccount")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

}
