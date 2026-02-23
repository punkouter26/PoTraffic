using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.Api.Features.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("register", Register);
        group.MapPost("login", Login);
        group.MapPost("logout", Logout).RequireAuthorization();
        group.MapPost("refresh-token", RefreshToken);
        group.MapGet("confirm-email", ConfirmEmail);

        return app;
    }

    private static async Task<IResult> Register(ISender sender, [FromBody] RegisterRequest request)
    {
        try
        {
            RegisterResult result = await sender.Send(
                new RegisterCommand(request.Email, request.Password, request.Locale));
            return result.IsSuccess
                ? Results.Created("/api/account/profile", result.Response)
                : Results.Conflict(new { error = result.ErrorCode });
        }
        catch (FluentValidation.ValidationException ex)
            when (ex.Errors.Any(e => e.PropertyName == "Email"
                                   && e.ErrorMessage.Contains("already registered", StringComparison.OrdinalIgnoreCase)))
        {
            // FR-013: duplicate email registration → 409 Conflict
            return Results.Conflict(new { error = "DUPLICATE_EMAIL" });
        }
    }

    private static async Task<IResult> Login(ISender sender, [FromBody] LoginRequest request)
    {
        LoginResult result = await sender.Send(new LoginCommand(request.Email, request.Password));
        return result.IsSuccess ? Results.Ok(result.Response) : Results.Unauthorized();
    }

    private static IResult Logout() => Results.NoContent();

    private static async Task<IResult> RefreshToken(ISender sender, [FromBody] RefreshTokenRequest request)
    {
        RefreshTokenResult result = await sender.Send(new RefreshTokenCommand(request.RefreshToken));
        return result.IsSuccess ? Results.Ok(result.Response) : Results.Unauthorized();
    }

    private static async Task<IResult> ConfirmEmail([FromQuery] string token, PoTrafficDbContext db)
    {
        User? user = await db.Set<User>().FirstOrDefaultAsync(u => u.EmailVerificationToken == token);
        if (user is null) return Results.NotFound();
        user.IsEmailVerified = true;
        user.EmailVerificationToken = null;
        await db.SaveChangesAsync();
        return Results.NoContent();
    }
}

public sealed record RefreshTokenRequest(string RefreshToken);
