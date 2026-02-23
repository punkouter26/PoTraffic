using PoTraffic.Api.Features.Auth;

namespace PoTraffic.IntegrationTests.Helpers;

public sealed class FakeExternalIdentityProvider : IExternalIdentityProvider
{
    private readonly string _provider;

    public FakeExternalIdentityProvider(string provider)
    {
        _provider = provider;
    }

    public string ProviderName => _provider;

    public bool IsConfigured() => true;

    public string BuildAuthorizationUrl(string redirectUri, string state)
    {
        return $"https://fake-{_provider}.invalid/authorize?state={Uri.EscapeDataString(state)}";
    }

    public Task<ExternalIdentity?> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        if (!string.Equals(code, "integration-test-code", StringComparison.Ordinal))
            return Task.FromResult<ExternalIdentity?>(null);

        string email = $"{_provider}.user@test.invalid";
        return Task.FromResult<ExternalIdentity?>(new ExternalIdentity(
            Subject: $"{_provider}-subject-001",
            Email: email,
            IsEmailVerified: true));
    }
}
