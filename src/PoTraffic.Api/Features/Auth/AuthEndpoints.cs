using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using PoTraffic.Api.Infrastructure.Security;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.Api.Features.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/auth").WithTags("Auth");

        // Sign-in is Microsoft OAuth only outside the Testing environment.
        // Sessions are server-managed HttpOnly cookies (BFF) — no tokens reach the client.
        group.MapGet("providers", GetAvailableProviders);
        group.MapGet("me", Me).RequireAuthorization();
        // Cast: (HttpContext) => Task<IResult> would otherwise bind as a RequestDelegate
        group.MapPost("logout", (Delegate)Logout);
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

    private static IResult Me(ClaimsPrincipal user)
    {
        Guid? userId = user.GetUserIdOrNull();
        if (userId is null) return Results.Unauthorized();

        return Results.Ok(new AuthMeResponse(
            userId.Value,
            user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email") ?? string.Empty,
            user.FindFirstValue(ClaimTypes.Role) ?? "Commuter",
            user.FindFirstValue("auth_provider") ?? string.Empty));
    }

    private static async Task<IResult> Logout(HttpContext httpContext)
    {
        await httpContext.SignOutAsync();
        return Results.NoContent();
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
            return Results.Redirect("/login?error=INVALID_CALLBACK");
        }

        string redirectUri = BuildExternalCallbackUri(httpContext, provider);
        ExternalAuthCompletionResult result = await externalAuthService.CompleteLoginAsync(
            provider,
            code,
            state,
            redirectUri,
            ct);

        if (!result.IsSuccess || result.User is null)
        {
            return Results.Redirect($"/login?error={Uri.EscapeDataString(result.ErrorCode ?? "EXTERNAL_AUTH_FAILED")}");
        }

        // BFF: the session is established server-side; the browser lands directly
        // on the requested page with an HttpOnly cookie — no tokens in the URL.
        await CookieSignIn.SignInAsync(httpContext, result.User);
        return Results.Redirect(result.ReturnPath);
    }

    private static string BuildExternalCallbackUri(HttpContext httpContext, string provider)
    {
        HttpRequest request = httpContext.Request;
        return $"{request.Scheme}://{request.Host}{request.PathBase}/api/auth/external/{provider}/callback";
    }
}
