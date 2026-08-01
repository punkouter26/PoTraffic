using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace PoTraffic.API.Infrastructure.Security;

/// <summary>
/// Shared user-ID extraction logic so every endpoint file does not repeat the
/// NameIdentifier / sub / Identity.Name fallback chain.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Returns the authenticated user's ID, or <see cref="UserId.Empty"/> when the claim
    /// cannot be parsed. Use in endpoints that already enforce [Authorize] and treat an
    /// empty ID as "not found" at the database level.
    /// </summary>
    public static UserId GetUserId(this ClaimsPrincipal user)
    {
        string? raw = user.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? user.FindFirstValue("sub");
        return UserId.TryParse(raw, null, out UserId id) ? id : UserId.Empty;
    }

    /// <summary>
    /// Returns the authenticated user's ID, or <c>null</c> when the claim cannot be parsed.
    /// Logs a warning when <paramref name="logger"/> is supplied and extraction fails.
    /// Use in endpoints that return 401 on missing identity.
    /// </summary>
    public static UserId? GetUserIdOrNull(this ClaimsPrincipal user, ILogger? logger = null)
    {
        string? raw = user.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? user.FindFirstValue("sub")
                   ?? user.Identity?.Name;

        if (UserId.TryParse(raw, null, out UserId id))
            return id;

        logger?.LogWarning(
            "[Auth] GetUserIdOrNull failed. Raw: '{Raw}', Claims: {Claims}",
            raw ?? "NULL",
            string.Join(", ", user.Claims.Take(5).Select(c => $"{c.Type}={c.Value}")));

        return null;
    }
}
