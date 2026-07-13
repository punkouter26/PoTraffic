using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;

namespace PoTraffic.Api.Infrastructure;

internal static class KeyVaultConfigurationExtensions
{
    /// <summary>
    /// Adds Azure Key Vault to the configuration pipeline when <c>KeyVault:Uri</c> is set.
    /// Must run BEFORE service registrations — services read secrets eagerly at registration
    /// time, so Key Vault must resolve first (else they get the appsettings placeholders).
    /// <para>
    /// <see cref="PrefixKeyVaultSecretManager"/> strips the <c>PoTraffic--</c> namespace prefix
    /// (e.g. <c>PoTraffic--ConnectionStrings--Default</c> → <c>ConnectionStrings:Default</c>) and
    /// secrets reload every 30 minutes so rotated values apply without a restart.
    /// </para>
    /// <para>
    /// Rule 10 (First-Run Success): in Development a credential/auth failure is swallowed so a
    /// fresh checkout without <c>az login</c> still boots off appsettings.Development.json. In
    /// Production/Staging a Key Vault failure is a hard error — secrets MUST come from the vault.
    /// </para>
    /// </summary>
    internal static WebApplicationBuilder AddPoTrafficKeyVault(this WebApplicationBuilder builder)
    {
        string? vaultUri = builder.Configuration["KeyVault:Uri"];
        if (string.IsNullOrWhiteSpace(vaultUri))
            return builder;

        var kvOptions = new AzureKeyVaultConfigurationOptions
        {
            Manager = new PrefixKeyVaultSecretManager(),
            ReloadInterval = TimeSpan.FromMinutes(30)
        };

        if (builder.Environment.IsDevelopment())
        {
            // Try DefaultAzureCredential (az login is the typical dev path); on any
            // credential/auth failure fall back to appsettings.Development.json silently.
            try
            {
                builder.Configuration.AddAzureKeyVault(
                    new Uri(vaultUri), new DefaultAzureCredential(), kvOptions);
            }
            catch (Exception ex) when (
                ex is AuthenticationFailedException      // covers CredentialUnavailableException
                || ex is Azure.RequestFailedException
                || ex is AggregateException)
            {
                Console.WriteLine(
                    $"[startup] Key Vault unreachable ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}); " +
                    "falling back to appsettings.Development.json (DEV-ONLY). " +
                    "Run `az login` and ensure the vault subscription is enabled to load secrets from Key Vault.");
            }

            return builder;
        }

        // Production / Staging — pin the credential to a single managed identity so a
        // "wrong MI" mistake fails deterministically instead of silently chaining.
        // AZURE_CLIENT_ID (App Service app setting, from Bicep) selects the shared
        // user-assigned MI; if unset, fall back to system-assigned — never chain.
        string? clientId = builder.Configuration["AZURE_CLIENT_ID"];
        Azure.Core.TokenCredential credential = !string.IsNullOrWhiteSpace(clientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId))
            : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);

        builder.Configuration.AddAzureKeyVault(new Uri(vaultUri), credential, kvOptions);
        return builder;
    }
}
