using Microsoft.AspNetCore.Authorization;

namespace PoTraffic.Api.Infrastructure.Security;

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
///   <item><c>auth_provider = "guest"</c>      → allowed in Testing only.</item>
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

        // Development/Production/Staging: require Microsoft OAuth.
        string? provider = ctx.User.FindFirst("auth_provider")?.Value;
        return string.Equals(provider, "microsoft", StringComparison.OrdinalIgnoreCase);
    }
}
