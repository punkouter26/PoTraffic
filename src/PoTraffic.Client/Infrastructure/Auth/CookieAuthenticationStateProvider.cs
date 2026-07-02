using System.Net.Http.Json;
using System.Security.Claims;
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

            AuthMeResponse? me = await response.Content.ReadFromJsonAsync<AuthMeResponse>();
            if (me is null)
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

    /// <summary>Signs out server-side and reverts to an anonymous identity.</summary>
    public async Task LogoutAsync()
    {
        await http.PostAsync("/api/auth/logout", content: null);
        NotifyAuthenticationStateChanged(Task.FromResult(Anonymous()));
    }

    /// <summary>Re-queries /api/auth/me (e.g. after a guest login established a cookie).</summary>
    public void Refresh() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    private static AuthenticationState Anonymous() =>
        new(new ClaimsPrincipal(new ClaimsIdentity()));
}
