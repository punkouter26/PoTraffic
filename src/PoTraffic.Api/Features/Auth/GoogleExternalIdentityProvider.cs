using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace PoTraffic.Api.Features.Auth;

public sealed class GoogleExternalIdentityProvider : IExternalIdentityProvider
{
    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<ExternalAuthConfiguration> _options;

    public GoogleExternalIdentityProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<ExternalAuthConfiguration> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public string ProviderName => "google";

    public bool IsConfigured()
    {
        ExternalAuthConfiguration.ProviderOptions cfg = _options.Value.Google;
        return cfg.Enabled
               && !string.IsNullOrWhiteSpace(cfg.ClientId)
               && !string.IsNullOrWhiteSpace(cfg.ClientSecret);
    }

    public string BuildAuthorizationUrl(string redirectUri, string state)
    {
        ExternalAuthConfiguration.ProviderOptions cfg = _options.Value.Google;
        string scopes = string.Join(' ', cfg.Scopes.Length == 0 ? ["openid", "email", "profile"] : cfg.Scopes);

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = cfg.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scopes,
            ["state"] = state,
            ["access_type"] = "offline",
            ["prompt"] = "select_account"
        };

        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(AuthorizationEndpoint, query!);
    }

    public async Task<ExternalIdentity?> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        ExternalAuthConfiguration.ProviderOptions cfg = _options.Value.Google;
        HttpClient http = _httpClientFactory.CreateClient();

        using HttpResponseMessage tokenResponse = await http.PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = cfg.ClientId,
                ["client_secret"] = cfg.ClientSecret,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code"
            }),
            ct);

        if (!tokenResponse.IsSuccessStatusCode)
            return null;

        GoogleTokenResponse? token = await tokenResponse.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken: ct);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            return null;

        using HttpRequestMessage request = new(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);

        using HttpResponseMessage userInfoResponse = await http.SendAsync(request, ct);
        if (!userInfoResponse.IsSuccessStatusCode)
            return null;

        GoogleUserInfoResponse? userInfo = await userInfoResponse.Content.ReadFromJsonAsync<GoogleUserInfoResponse>(cancellationToken: ct);
        if (userInfo is null || string.IsNullOrWhiteSpace(userInfo.Sub) || string.IsNullOrWhiteSpace(userInfo.Email))
            return null;

        return new ExternalIdentity(userInfo.Sub, userInfo.Email, userInfo.EmailVerified);
    }

    private sealed record GoogleTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("id_token")] string? IdToken);

    private sealed record GoogleUserInfoResponse(
        [property: JsonPropertyName("sub")] string Sub,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("email_verified")] bool EmailVerified);
}
