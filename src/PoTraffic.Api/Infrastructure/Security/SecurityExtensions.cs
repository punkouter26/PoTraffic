using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using PoTraffic.Api.Features.Auth;

namespace PoTraffic.Api.Infrastructure.Security;

internal static class SecurityExtensions
{
    /// <summary>
    /// Registers JWT Bearer authentication, authorization policies, DataProtection, and external OAuth providers.
    /// Strategy pattern — external provider implementation is selected by provider key at runtime.
    /// </summary>
    internal static IServiceCollection AddSecurityServices(this IServiceCollection services, IConfiguration configuration)
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
                    ValidateIssuer           = true,
                    ValidateAudience         = true,
                    ValidateLifetime         = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer              = jwtCfg.Issuer,
                    ValidAudience            = jwtCfg.Audience,
                    IssuerSigningKey         = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwtCfg.Key)) { KeyId = "potraffic-key" },
                    ClockSkew = TimeSpan.Zero,
                    // Ensure "sub" and "role" claims are mapped correctly to User.Identity properties
                    NameClaimType = "sub",
                    RoleClaimType = ClaimTypes.Role
                };
            });

        services.AddAuthorization(opts =>
        {
            opts.AddPolicy("AdminOnly", p => p.RequireRole("Administrator"));
        });

        services.AddSingleton<JwtTokenService>();
        services.AddDataProtection();
        services.AddHttpClient();
        services.AddScoped<IExternalIdentityProvider, GoogleExternalIdentityProvider>();
        services.AddScoped<IExternalIdentityProvider, MicrosoftExternalIdentityProvider>();
        services.AddScoped<ExternalAuthService>();

        return services;
    }
}
