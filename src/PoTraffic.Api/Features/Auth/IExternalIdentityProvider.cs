namespace PoTraffic.Api.Features.Auth;

public interface IExternalIdentityProvider
{
    string ProviderName { get; }
    bool IsConfigured();
    string BuildAuthorizationUrl(string redirectUri, string state);
    Task<ExternalIdentity?> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct);
}
