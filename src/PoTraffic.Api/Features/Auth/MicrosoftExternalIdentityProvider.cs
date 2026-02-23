using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace PoTraffic.Api.Features.Auth;

public sealed class MicrosoftExternalIdentityProvider : IExternalIdentityProvider
{
    private const string Authority = "https://login.microsoftonline.com/common/oauth2/v2.0";
    private const string UserInfoEndpoint = "https://graph.microsoft.com/oidc/userinfo";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<ExternalAuthConfiguration> _options;

    public MicrosoftExternalIdentityProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<ExternalAuthConfiguration> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
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
        HttpClient http = _httpClientFactory.CreateClient();

        using HttpResponseMessage tokenResponse = await http.PostAsync(
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
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            return null;

        using HttpRequestMessage request = new(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);

        using HttpResponseMessage userInfoResponse = await http.SendAsync(request, ct);
        if (!userInfoResponse.IsSuccessStatusCode)
            return null;

        MicrosoftUserInfoResponse? userInfo = await userInfoResponse.Content.ReadFromJsonAsync<MicrosoftUserInfoResponse>(cancellationToken: ct);
        if (userInfo is null || string.IsNullOrWhiteSpace(userInfo.Sub))
            return null;

        string email = userInfo.Email ?? userInfo.PreferredUsername ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email))
            return null;

        bool isEmailVerified = userInfo.EmailVerified ?? !string.IsNullOrWhiteSpace(userInfo.Email);
        return new ExternalIdentity(userInfo.Sub, email, isEmailVerified);
    }

    private sealed record MicrosoftTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("id_token")] string? IdToken);

    private sealed record MicrosoftUserInfoResponse(
        [property: JsonPropertyName("sub")] string Sub,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("preferred_username")] string? PreferredUsername,
        [property: JsonPropertyName("email_verified")] bool? EmailVerified);
}
