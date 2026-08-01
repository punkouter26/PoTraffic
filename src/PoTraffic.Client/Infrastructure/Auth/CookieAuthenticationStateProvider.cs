using System.Net.Http.Json;
using System.Security.Claims;
using PoTraffic.Client.Infrastructure.Http;
using Microsoft.AspNetCore.Components.Authorization;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.Client.Infrastructure.Auth;

/// <summary>
/// BFF <see cref="AuthenticationStateProvider"/> — the session lives in a
/// server-managed HttpOnly cookie the WASM app can never read. Auth state is
/// derived from GET /api/auth/me; the client holds no tokens.
/// </summary>
public sealed class CookieAuthenticationStateProvider(HttpClient http) : AuthenticationStateProvider
{
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync("/api/auth/me");
            if (!response.IsSuccessStatusCode)
                return Anonymous();

            AuthMeResponse? me = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.AuthMeResponse);
            // /api/auth/me now returns 200 with an empty UserId for anonymous callers
            // (avoids a console 401 on first paint), so treat the sentinel as signed-out.
            if (me is null || me.UserId.IsEmpty)
                return Anonymous();

            ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.NameIdentifier, me.UserId.ToString()),
                new Claim(ClaimTypes.Name, me.Email),
                new Claim(ClaimTypes.Email, me.Email),
                new Claim("email", me.Email),
                new Claim(ClaimTypes.Role, me.Role),
                new Claim("auth_provider", me.AuthProvider),
            ], authenticationType: "cookie", ClaimTypes.Name, ClaimTypes.Role);

            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch (HttpRequestException)
        {
            return Anonymous();
        }
    }

    /// <summary>Signs out server-side and reverts to an anonymous identity. Best-effort:
    /// a failed/aborted server call (offline, connection reset, mid-navigation) must still
    /// sign the user out locally rather than surfacing an error page.</summary>
    public async Task LogoutAsync()
    {
        try
        {
            using var response = await http.PostAsync("/api/auth/logout", content: null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Cookie will expire server-side regardless; proceed to local sign-out.
        }
        NotifyAuthenticationStateChanged(Task.FromResult(Anonymous()));
    }

    /// <summary>Re-queries /api/auth/me (e.g. after a guest login established a cookie).</summary>
    public void Refresh() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    private static AuthenticationState Anonymous() =>
        new(new ClaimsPrincipal(new ClaimsIdentity()));
}
