using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using PoTraffic.Api.Features.Auth;

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
                options.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
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
        });

        // Persist Data Protection keys (cookie + OAuth state encryption) to the local
        // file system. For multi-instance deployments a Table Storage key ring would be
        // the follow-up.
        services.AddDataProtection()
            .SetApplicationName("PoTraffic")
            .PersistKeysToFileSystem(
                new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "keys")));
        services.AddHttpClient();
        // Microsoft OAuth is the only external sign-in provider. Local password
        // login/registration and Google sign-in were removed by design.
        services.AddScoped<IExternalIdentityProvider, MicrosoftExternalIdentityProvider>();
        services.AddScoped<ExternalAuthService>();

        return services;
    }
}
