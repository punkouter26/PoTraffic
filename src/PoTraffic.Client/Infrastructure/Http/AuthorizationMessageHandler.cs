using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components;
using PoTraffic.Client.Infrastructure.Auth;

using Microsoft.JSInterop;

namespace PoTraffic.Client.Infrastructure.Http;

/// <summary>
/// DelegatingHandler that attaches the current JWT bearer token to every outgoing
/// API request and triggers a logout + redirect to /login on a 401 Unauthorized
/// response (e.g. token has expired while the user is on a polling page).
///
/// Strategy pattern — selected at startup by wiring this handler into IHttpClientFactory,
/// decoupling token lifecycle from individual components.
/// </summary>
public sealed class AuthorizationMessageHandler : DelegatingHandler
{
    private readonly JwtAuthenticationStateProvider _authProvider;
    private readonly NavigationManager _nav;

    public AuthorizationMessageHandler(
        JwtAuthenticationStateProvider authProvider,
        NavigationManager nav)
    {
        _authProvider = authProvider;
        _nav = nav;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        // Attach the latest token fresh from localStorage before each request so
        // expiry-related issues are caught immediately rather than relying on
        // the stale DefaultRequestHeaders set at login time.
        string? token = await _authProvider.GetAccessTokenAsync();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response = await base.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Token has expired or been revoked — clear local auth state and redirect.
            await _authProvider.LogoutAsync();
            _nav.NavigateTo("/login");
        }

        return response;
    }
}
