using Microsoft.AspNetCore.Authorization;

namespace PoTraffic.API.Infrastructure.Security;

/// <summary>
/// Rule 13 — In Production, Microsoft OAuth is the only accepted identity provider.
///
/// This policy is enforced on protected route groups.
/// In <c>Development</c>, <c>Production</c>, and <c>Staging</c>, Microsoft OAuth
/// is the only accepted identity provider. In <c>Testing</c> the policy is a
/// no-op, so E2E and integration tests can use deterministic JWTs.
///
/// <para>Claim shape (set in <c>CookieSignIn</c>):</para>
/// <list type="bullet">
///   <item><c>auth_provider = "microsoft"</c> → allowed in all environments.</item>
///   <item><c>auth_provider = "guest"</c>      → allowed in Development and Testing.</item>
///   <item>Testing JWTs without <c>auth_provider</c> → allowed in Testing only.</item>
/// </list>
/// </summary>
internal static class ProductionMicrosoftAuthPolicy
{
    public static bool Evaluate(string environmentName, AuthorizationHandlerContext ctx)
    {
        // Testing: allow deterministic test principals.
        if (string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase))
        {
            return ctx.User.Identity?.IsAuthenticated == true;
        }

        string? provider = ctx.User.FindFirst("auth_provider")?.Value;

        // Development: Microsoft OAuth or the guest bypass (Rule 4.4 split view).
        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(provider, "microsoft", StringComparison.OrdinalIgnoreCase)
                || string.Equals(provider, "guest", StringComparison.OrdinalIgnoreCase);
        }

        // Production/Staging: require Microsoft OAuth.
        return string.Equals(provider, "microsoft", StringComparison.OrdinalIgnoreCase);
    }
}
