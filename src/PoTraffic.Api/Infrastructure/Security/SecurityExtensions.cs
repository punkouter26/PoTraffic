using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using PoTraffic.Api.Features.Auth;
using PoTraffic.Api.Infrastructure.Data;

namespace PoTraffic.Api.Infrastructure.Security;

internal static class SecurityExtensions
{
    /// <summary>
    /// Registers JWT Bearer authentication, authorization policies, DataProtection, and external OAuth providers.
    /// Strategy pattern — external provider implementation is selected by provider key at runtime.
    /// </summary>
    internal static IServiceCollection AddSecurityServices(this IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        JwtConfiguration jwtCfg = configuration.GetSection("Jwt").Get<JwtConfiguration>()
            ?? throw new InvalidOperationException("Jwt configuration section is missing.");

        services.Configure<JwtConfiguration>(configuration.GetSection("Jwt"));
        services.Configure<ExternalAuthConfiguration>(configuration.GetSection("ExternalAuth"));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtCfg.Issuer,
                    ValidAudience = jwtCfg.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwtCfg.Key))
                    { KeyId = "potraffic-key" },
                    ClockSkew = TimeSpan.Zero,
                    // Ensure "sub" and "role" claims are mapped correctly to User.Identity properties
                    NameClaimType = "sub",
                    RoleClaimType = ClaimTypes.Role
                };
            });

        services.AddAuthorization(opts =>
        {
            opts.AddPolicy("AdminOnly", p => p.RequireRole("Administrator"));

            // Rule 13: in Production, Microsoft OAuth is REQUIRED.
            // GUEST and password-only sessions are rejected. The policy is a no-op
            // in non-Production environments so dev/test E2E flows keep working.
            opts.AddPolicy("ProductionMicrosoftAuth", p =>
            {
                p.RequireAuthenticatedUser();
                p.RequireAssertion(ctx => ProductionMicrosoftAuthPolicy.Evaluate(environmentName, ctx));
            });
        });

        services.AddSingleton<JwtTokenService>();
        // Persist Data Protection keys to SQL so they survive app restarts and deployments.
        // Without persistence, the encrypted OAuth state (nonce) cannot be decrypted after
        // a restart, causing INVALID_STATE errors on the external login callback.
        services.AddDataProtection()
            .SetApplicationName("PoTraffic")
            .PersistKeysToDbContext<PoTrafficDbContext>();
        services.AddHttpClient();
        services.AddScoped<IExternalIdentityProvider, GoogleExternalIdentityProvider>();
        services.AddScoped<IExternalIdentityProvider, MicrosoftExternalIdentityProvider>();
        services.AddScoped<ExternalAuthService>();

        return services;
    }
}
