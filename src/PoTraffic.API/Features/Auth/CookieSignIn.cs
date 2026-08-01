using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace PoTraffic.API.Features.Auth;

/// <summary>
/// BFF cookie session issuance — the Blazor WASM client never sees a token.
/// The HttpOnly SameSite=Strict cookie is the only session credential.
/// </summary>
public static class CookieSignIn
{
    public static Task SignInAsync(HttpContext httpContext, User user)
    {
        ClaimsIdentity identity = new(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Email),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim("email", user.Email),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("auth_provider", user.AuthProvider),
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        return httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }
}
