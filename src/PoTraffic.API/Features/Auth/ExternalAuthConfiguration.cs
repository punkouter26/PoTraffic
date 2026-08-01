namespace PoTraffic.API.Features.Auth;

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

        /// <summary>
        /// Allowed Entra/AAD tenant ids for the shape-based issuer validator (§4.3).
        /// Empty = accept any Microsoft tenant (work/school + personal), matching the
        /// <c>AzureADandPersonalMicrosoftAccount</c> audience.
        /// </summary>
        public string[] AllowedTenantIds { get; init; } = [];
    }
}
