using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Security.KeyVault.Secrets;

namespace PoTraffic.Api.Infrastructure;

/// <summary>
/// Strategy pattern — swaps the secret-name-to-config-key mapping strategy so that
/// Key Vault secrets prefixed with "PoTraffic--" are stripped of the prefix before
/// being inserted into the ASP.NET Core configuration hierarchy.
///
/// Without prefix stripping, the secret "PoTraffic--ConnectionStrings--Default"
/// would map to config key "PoTraffic:ConnectionStrings:Default" (unusable),
/// but with stripping it correctly maps to "ConnectionStrings:Default".
///
/// All secrets without the expected prefix are excluded from configuration loading,
/// keeping the shared vault tidy for multi-app scenarios.
/// </summary>
internal sealed class PrefixKeyVaultSecretManager : KeyVaultSecretManager
{
    private const string Prefix = "PoTraffic--";

    /// <inheritdoc />
    /// <remarks>Only load secrets whose name begins with the app-specific prefix.</remarks>
    public override bool Load(SecretProperties properties)
        => properties.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    /// <remarks>
    /// Strip the prefix, then replace remaining "--" with ":" to produce
    /// the ASP.NET Core configuration key (e.g. "ConnectionStrings:Default").
    /// </remarks>
    public override string GetKey(KeyVaultSecret secret)
        => secret.Name[Prefix.Length..]
                  .Replace("--", ConfigurationPath.KeyDelimiter);
}
