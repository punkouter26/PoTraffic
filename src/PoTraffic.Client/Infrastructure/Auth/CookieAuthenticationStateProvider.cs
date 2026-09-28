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
public sealed class CookieAuthenticationStateProvider(HttpClient http, ClientCache cache)
    : AuthenticationStateProvider
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

            // Namespace cached snapshots to this identity. Without it, a shared browser
            // would serve the previous account's routes to whoever signs in next.
            cache.UseScope(me.UserId.ToString());

            // Tell the server which zone this browser is in, once per app load: every
            // per-slot statistic buckets in it. Fire-and-forget — the server only writes
            // when it changed, and a failure just leaves the previous zone in place.
            if (!_zoneReported)
            {
                _zoneReported = true;
                _ = ReportTimeZoneAsync();
            }

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

    private bool _zoneReported;

    private async Task ReportTimeZoneAsync()
    {
        try
        {
            using HttpResponseMessage _ = await http.PutAsJsonAsync("/api/account/timezone",
                new SetTimeZoneRequest(TimeZoneInfo.Local.Id), AppJsonContext.Default.SetTimeZoneRequest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
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

        // Cached snapshots and cached API responses outlive the cookie, so signing out
        // has to take them with it.
        await cache.ClearAllAsync();
        _zoneReported = false;

        NotifyAuthenticationStateChanged(Task.FromResult(Anonymous()));
    }

    /// <summary>Re-queries /api/auth/me (e.g. after a guest login established a cookie).</summary>
    public void Refresh() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    private static AuthenticationState Anonymous() =>
        new(new ClaimsPrincipal(new ClaimsIdentity()));
}
