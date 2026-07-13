using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace PoTraffic.Api.Features.Auth;

public sealed class MicrosoftExternalIdentityProvider : IExternalIdentityProvider
{
    private const string Authority = "https://login.microsoftonline.com/common/oauth2/v2.0";

    private readonly HttpClient _http;
    private readonly IOptions<ExternalAuthConfiguration> _options;
    private readonly MicrosoftIdTokenValidator _idTokenValidator;

    public MicrosoftExternalIdentityProvider(
        HttpClient http,
        IOptions<ExternalAuthConfiguration> options,
        MicrosoftIdTokenValidator idTokenValidator)
    {
        _http = http;
        _options = options;
        _idTokenValidator = idTokenValidator;
    }

    public string ProviderName => "microsoft";

    public bool IsConfigured()
    {
        ExternalAuthConfiguration.ProviderOptions cfg = _options.Value.Microsoft;
        return cfg.Enabled
               && !string.IsNullOrWhiteSpace(cfg.ClientId)
               && !string.IsNullOrWhiteSpace(cfg.ClientSecret);
    }

    public string BuildAuthorizationUrl(string redirectUri, string state)
    {
        ExternalAuthConfiguration.ProviderOptions cfg = _options.Value.Microsoft;
        string scopes = string.Join(' ', cfg.Scopes.Length == 0 ? ["openid", "email", "profile"] : cfg.Scopes);

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = cfg.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["response_mode"] = "query",
            ["scope"] = scopes,
            ["state"] = state,
            ["prompt"] = "select_account"
        };

        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString($"{Authority}/authorize", query!);
    }

    public async Task<ExternalIdentity?> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        ExternalAuthConfiguration.ProviderOptions cfg = _options.Value.Microsoft;

        using HttpResponseMessage tokenResponse = await _http.PostAsync(
            $"{Authority}/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = cfg.ClientId,
                ["client_secret"] = cfg.ClientSecret,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["scope"] = string.Join(' ', cfg.Scopes.Length == 0 ? ["openid", "email", "profile"] : cfg.Scopes)
            }),
            ct);

        if (!tokenResponse.IsSuccessStatusCode)
            return null;

        MicrosoftTokenResponse? token = await tokenResponse.Content.ReadFromJsonAsync<MicrosoftTokenResponse>(cancellationToken: ct);
        if (token is null || string.IsNullOrWhiteSpace(token.IdToken))
            return null;

        // §4.3 — trust the identity ONLY after validating the id_token's signature (JWKS),
        // audience, lifetime, and issuer/tenant. The token arrives over the server-to-server
        // TLS code exchange, but we still verify it cryptographically rather than trusting
        // an unauthenticated Graph userinfo call.
        return await _idTokenValidator.ValidateAsync(token.IdToken, cfg.ClientId, cfg.AllowedTenantIds, ct);
    }

    private sealed record MicrosoftTokenResponse(
        [property: JsonPropertyName("id_token")] string? IdToken);
}
