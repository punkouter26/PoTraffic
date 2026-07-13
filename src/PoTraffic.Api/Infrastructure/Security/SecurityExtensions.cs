using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using PoTraffic.Api.Features.Auth;
using PoTraffic.Api.Infrastructure.Resilience;

namespace PoTraffic.Api.Infrastructure.Security;

internal static class SecurityExtensions
{
    /// <summary>
    /// Registers BFF cookie authentication, authorization policies, DataProtection,
    /// and the external OAuth provider. The Blazor WASM client never handles tokens —
    /// the HttpOnly SameSite=Strict cookie is the only session credential.
    /// </summary>
    internal static IServiceCollection AddSecurityServices(this IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        services.Configure<ExternalAuthConfiguration>(configuration.GetSection("ExternalAuth"));

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = ".PoTraffic.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromDays(14);
                // API host — return status codes instead of redirecting to a login page.
                // Fix #2 — include WWW-Authenticate and a JSON hint so a 401 isn't
                // a dead end for first-time users (browser, CLI, or wasm client).
                options.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.Headers["WWW-Authenticate"] =
                        "Cookie realm=\"PoTraffic\", path=\"/.auth/login\"";
                    // Skip the body for non-HTML requests (API, framework assets) so
                    // the WASM boot.json loader sees a clean 401 it can retry on.
                    if (!ctx.Request.Path.StartsWithSegments("/_framework") &&
                        !ctx.Request.Path.StartsWithSegments("/_content"))
                    {
                        ctx.Response.ContentType = "application/problem+json";
                        return ctx.Response.WriteAsync(
                            "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.2\"," +
                            "\"title\":\"Unauthorized\",\"status\":401," +
                            "\"detail\":\"Session expired or absent. Sign in at /login.\"," +
                            "\"loginUrl\":\"/login\"}");
                    }
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    ctx.Response.Headers["WWW-Authenticate"] = "Cookie realm=\"PoTraffic\"";
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorization(opts =>
        {
            opts.AddPolicy("AdminOnly", p => p.RequireRole("Administrator"));

            // Rule 13: in Development/Production/Staging, Microsoft OAuth is REQUIRED.
            // GUEST sessions are rejected. The policy is a no-op only in Testing so
            // integration/E2E flows keep working.
            opts.AddPolicy("ProductionMicrosoftAuth", p =>
            {
                p.RequireAuthenticatedUser();
                p.RequireAssertion(ctx => ProductionMicrosoftAuthPolicy.Evaluate(environmentName, ctx));
            });

            // §4.5 — deny-by-default. Any endpoint WITHOUT explicit authorization metadata
            // now requires an authenticated user, so a forgotten [RequireAuthorization] can
            // never leave a feature endpoint anonymous. Genuinely public surfaces (auth/login
            // flow, /health, /diag, SPA shell + static assets) opt out via .AllowAnonymous().
            opts.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        // Persist Data Protection keys (cookie + OAuth state encryption) to the local
        // file system. For multi-instance deployments a Table Storage key ring would be
        // the follow-up.
        services.AddDataProtection()
            .SetApplicationName("PoTraffic")
            .PersistKeysToFileSystem(
                new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "keys")));
        // §4.3 — singleton id_token validator: caches/auto-refreshes the /common JWKS.
        services.AddSingleton<MicrosoftIdTokenValidator>();
        // Typed Microsoft OAuth client — resilience wired via the "external-auth" pipeline.
        services.AddHttpClient<MicrosoftExternalIdentityProvider>()
            .AddResilienceHandler(ResiliencePipelineExtensions.ExternalAuthPipeline);
        // Microsoft OAuth is the only external sign-in provider. Local password
        // login/registration and Google sign-in were removed by design.
        services.AddScoped<IExternalIdentityProvider, MicrosoftExternalIdentityProvider>();
        services.AddScoped<ExternalAuthService>();

        return services;
    }
}
