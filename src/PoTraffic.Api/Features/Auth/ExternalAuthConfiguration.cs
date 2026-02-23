namespace PoTraffic.Api.Features.Auth;

public sealed class ExternalAuthConfiguration
{
    public ProviderOptions Google { get; init; } = new();
    public ProviderOptions Microsoft { get; init; } = new();

    public sealed class ProviderOptions
    {
        public bool Enabled { get; init; }
        public string ClientId { get; init; } = string.Empty;
        public string ClientSecret { get; init; } = string.Empty;
        public string[] Scopes { get; init; } = [];
    }
}
