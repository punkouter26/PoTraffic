using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace PoTraffic.Client.Infrastructure.Auth;

/// <summary>
/// Custom <see cref="AuthenticationStateProvider"/> for Blazor WASM that reads a JWT
/// from <c>localStorage</c> and exposes the token's claims as the current identity.
/// Bearer token attachment is handled per-request by <c>AuthorizationMessageHandler</c>.
/// </summary>
public sealed class JwtAuthenticationStateProvider : AuthenticationStateProvider
{
    private const string TokenKey = "potraffic_access_token";

    private readonly IJSRuntime _js;

    public JwtAuthenticationStateProvider(IJSRuntime js)
    {
        _js = js;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        string? token = await GetAccessTokenAsync();

        if (string.IsNullOrWhiteSpace(token))
        {
            return Anonymous();
        }

        ClaimsPrincipal principal = ParseToken(token);

        if (principal.Identity?.IsAuthenticated != true)
        {
            return Anonymous();
        }

        return new AuthenticationState(principal);
    }

    /// <summary>Stores the JWT and notifies the framework that auth state changed.</summary>
    public async Task LoginAsync(string token)
    {
        await SetTokenAsync(token);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    /// <summary>Clears the JWT and reverts to an anonymous identity.</summary>
    public async Task LogoutAsync()
    {
        await RemoveTokenAsync();
        NotifyAuthenticationStateChanged(
            Task.FromResult(Anonymous()));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static AuthenticationState Anonymous() =>
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    private static ClaimsPrincipal ParseToken(string token)
    {
        try
        {
            JwtSecurityTokenHandler handler = new();
            JwtSecurityToken jwt = handler.ReadJwtToken(token);

            // Check expiry
            if (jwt.ValidTo < DateTime.UtcNow)
            {
                return new ClaimsPrincipal(new ClaimsIdentity());
            }

            // Start with all claims from the JWT
            List<Claim> claims = new(jwt.Claims);

            // Blazor and Radzen components often check for standard XML SOAP claim URIs
            // but JWTs use short names (sub, role, email). Map them if missing.

            // NameIdentifier mapping (sub -> NameIdentifier)
            if (!claims.Exists(c => c.Type == ClaimTypes.NameIdentifier))
            {
                string? sub = jwt.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;
                if (sub is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, sub));
            }

            // Name mapping (unique_name/sub/email -> Name)
            if (!claims.Exists(c => c.Type == ClaimTypes.Name))
            {
                string? name = jwt.Claims.FirstOrDefault(c => c.Type is "unique_name" or "sub" or "email")?.Value;
                if (name is not null) claims.Add(new Claim(ClaimTypes.Name, name));
            }

            // Role mapping (role -> Role)
            if (!claims.Exists(c => c.Type == ClaimTypes.Role))
            {
                // JWT roles might be an array or a single string. JwtSecurityTokenHandler
                // usually handles this, but since we're manually mapping, we check both short and long.
                var roleClaims = jwt.Claims.Where(c => c.Type is "role" or "http://schemas.microsoft.com/ws/2008/06/identity/claims/role");
                foreach (var rc in roleClaims)
                {
                    if (!claims.Exists(c => c.Type == ClaimTypes.Role && c.Value == rc.Value))
                    {
                        claims.Add(new Claim(ClaimTypes.Role, rc.Value));
                    }
                }
            }

            // Use the "jwt" authentication type to ensure IsAuthenticated is true.
            // Explicitly set the name and role claim types to ensure consistency.
            ClaimsIdentity identity = new(claims, "jwt", ClaimTypes.Name, ClaimTypes.Role);
            
            // Console logging to help debug E2E failures (can be removed in future cleanup)
            Console.WriteLine($"[AUTH_PROVIDER] Token parsed. Sub: {jwt.Subject}, Exp: {jwt.ValidTo}, Authenticated: {identity.IsAuthenticated}");
            
            return new ClaimsPrincipal(identity);
        }
        catch
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }
    }

    // ── localStorage interop ──────────────────────────────────────────

    /// <summary>Returns the raw JWT string from localStorage, or <c>null</c> if absent.</summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        try
        {
            return await _js.InvokeAsync<string?>("localStorage.getItem", TokenKey);
        }
        catch (InvalidOperationException)
        {
            // JS interop not available during prerendering
            return null;
        }
    }

    private async Task SetTokenAsync(string token) =>
        await _js.InvokeVoidAsync("localStorage.setItem", TokenKey, token);

    private async Task RemoveTokenAsync() =>
        await _js.InvokeVoidAsync("localStorage.removeItem", TokenKey);
}
