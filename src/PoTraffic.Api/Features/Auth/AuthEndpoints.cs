
using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Mvc;

using Microsoft.AspNetCore.Routing;



using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.Api.Features.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/auth").WithTags("Auth");

        // Sign-in is Microsoft OAuth only outside the Testing environment.
        // Local email/password registration and login were removed by design.
        group.MapGet("providers", GetAvailableProviders);
        group.MapPost("logout", Logout).RequireAuthorization("ProductionMicrosoftAuth");
        group.MapPost("refresh-token", RefreshToken);
        group.MapGet("external/{provider}/start", StartExternalLogin);
        group.MapGet("external/{provider}/callback", CompleteExternalLogin);

        return app;
    }

    private static IResult GetAvailableProviders(IEnumerable<IExternalIdentityProvider> providers)
    {
        var available = providers
            .Where(p => p.IsConfigured())
            .Select(p => p.ProviderName)
            .ToList();
        return Results.Ok(new { providers = available });
    }

    private static IResult Logout() => Results.NoContent();

    private static async Task<IResult> RefreshToken(ISender sender, [FromBody] RefreshTokenRequest request)
    {
        RefreshTokenResult result = await sender.Send(new RefreshTokenCommand(request.RefreshToken));
        return result.IsSuccess ? Results.Ok(result.Response) : Results.Unauthorized();
    }

    private static IResult StartExternalLogin(
        string provider,
        HttpContext httpContext,
        [FromQuery] string? returnUrl,
        ExternalAuthService externalAuthService)
    {
        try
        {
            string redirectUri = BuildExternalCallbackUri(httpContext, provider);
            string authorizationUrl = externalAuthService.BuildStartRedirectUrl(provider, redirectUri, returnUrl);
            return Results.Redirect(authorizationUrl);
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound(new { error = "UNSUPPORTED_PROVIDER" });
        }
    }

    private static async Task<IResult> CompleteExternalLogin(
        string provider,
        HttpContext httpContext,
        [FromQuery] string? code,
        [FromQuery] string? state,
        ExternalAuthService externalAuthService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return Results.Redirect("/auth/external-complete#error=INVALID_CALLBACK");
        }

        string redirectUri = BuildExternalCallbackUri(httpContext, provider);
        ExternalAuthCompletionResult result = await externalAuthService.CompleteLoginAsync(
            provider,
            code,
            state,
            redirectUri,
            ct);

        return Results.Redirect(ExternalAuthService.BuildCompletionRedirectPath(result));
    }

    private static string BuildExternalCallbackUri(HttpContext httpContext, string provider)
    {
        HttpRequest request = httpContext.Request;
        return $"{request.Scheme}://{request.Host}{request.PathBase}/api/auth/external/{provider}/callback";
    }
}

public sealed record RefreshTokenRequest(string RefreshToken);
