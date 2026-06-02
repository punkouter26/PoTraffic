using Microsoft.AspNetCore.Authorization;

namespace PoTraffic.Api.Infrastructure.Security;

/// <summary>
/// Rule 13 — In Production, Microsoft OAuth is the only accepted identity provider.
///
/// This policy is enforced alongside <c>RequireAuthorization()</c> on protected
/// route groups when the host environment is <c>Production</c> or <c>Staging</c>.
/// In <c>Development</c> and <c>Testing</c> the policy is a no-op, so GUEST
/// logins and password-only sessions continue to work for E2E + manual tests.
///
/// <para>Claim shape (set in <c>JwtTokenService</c>):</para>
/// <list type="bullet">
///   <item><c>auth_provider = "microsoft"</c> → allowed in all environments.</item>
///   <item><c>auth_provider = "google"</c>     → allowed in Dev/Test only.</item>
///   <item><c>auth_provider = "password"</c>   → allowed in Dev/Test only.</item>
///   <item><c>auth_provider = "guest"</c>      → allowed in Dev/Test only.</item>
/// </list>
/// </summary>
internal static class ProductionMicrosoftAuthPolicy
{
    public static bool Evaluate(string environmentName, AuthorizationHandlerContext ctx)
    {
        // Non-Production: allow any authenticated principal.
        if (!IsProdLike(environmentName))
        {
            return ctx.User.Identity?.IsAuthenticated == true;
        }

        // Production: require Microsoft OAuth.
        string? provider = ctx.User.FindFirst("auth_provider")?.Value;
        return string.Equals(provider, "microsoft", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProdLike(string env) =>
        string.Equals(env, "Production", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(env, "Staging", StringComparison.OrdinalIgnoreCase);
}
