using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using PoTraffic.Client.Infrastructure.Auth;
using PoTraffic.Shared.Constants;

namespace PoTraffic.Client.Layout;

/// <summary>
/// What "Account" means, once. The desktop dropdown (NavMenu) and the phone's bottom
/// sheet (BottomNav) each carried their own copy of the items, of the guest-label rule
/// and of sign-out — and had drifted: the sheet had no Admin entry at all.
/// </summary>
public static class AccountMenu
{
    public sealed record Link(string Href, string Icon, string Label);

    public sealed record Identity(bool IsAuthenticated, bool IsAdmin, bool IsGuest, string Label, string Initials);

    public static IReadOnlyList<Link> Links(bool isAdmin)
    {
        List<Link> links =
        [
            new("/account/settings", "settings", "Account settings"),
            new("/health", "health_and_safety", "System status"),
        ];
        if (isAdmin)
            links.Add(new("/admin", "admin_panel_settings", "Admin"));
        return links;
    }

    public static Identity Describe(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
            return new Identity(false, false, false, string.Empty, "?");

        bool isAdmin = HasRole(user, "Administrator");
        bool isGuest = HasRole(user, "Guest");

        string email = user.FindFirst("email")?.Value
            ?? user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.Identity?.Name
            ?? "User";

        // Guest accounts are minted as guest12345678@potraffic.dev; the raw address is noise,
        // so they render as GUEST12345678. The address shape is GuestAccountConstants'.
        return isGuest && GuestAccountConstants.IsGuestEmail(email)
            ? new Identity(true, isAdmin, true, GuestAccountConstants.ToDisplayLabel(email), "G")
            : new Identity(true, isAdmin, false, email, email[0].ToString().ToUpperInvariant());
    }

    public static async Task SignOutAsync(AuthenticationStateProvider auth, NavigationManager nav)
    {
        if (auth is CookieAuthenticationStateProvider cookie)
            await cookie.LogoutAsync();
        nav.NavigateTo("/login", forceLoad: true);
    }

    private static bool HasRole(ClaimsPrincipal user, string role) =>
        user.IsInRole(role) ||
        user.Claims.Any(c => c.Type is "role" or ClaimTypes.Role && c.Value == role);
}
