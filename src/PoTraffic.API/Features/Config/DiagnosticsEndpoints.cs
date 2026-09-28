// filepath: src/PoTraffic.API/Features/Config/DiagnosticsEndpoints.cs
// CI/CD rule #9 — Post-deployment smoke validation:
//   • GET /health (liveness + readiness)
//   • GET /diag/keyvault (MASKED Key Vault secret retrieval — proves identity wiring
//     and never returns the raw secret value to the caller)

using Microsoft.AspNetCore.Mvc;

namespace PoTraffic.API.Features.Config;

public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        // Every /diag* surface is admin-only and returns JSON. Rendering diagnostics as
        // JSON rather than hand-built HTML keeps configuration values — which are
        // attacker-influenceable in principle — out of an HTML sink entirely.
        RouteGroupBuilder group = app.MapGroup("/diag").WithTags("Diagnostics").RequireAuthorization("AdminOnly");

        // /diag/keyvault — only when ?secret= is supplied AND caller is admin
        // (otherwise just lists the secret NAMES the identity can see).
        group.MapGet("/keyvault", HandleKeyVaultDiag);
    }

    /// <summary>
    /// Reveals only the length bucket of a secret — never any of its characters. Showing
    /// a prefix would leak the discriminating part of most API keys.
    /// </summary>
    private static string Mask(string? value)
        => string.IsNullOrEmpty(value) ? "(empty)" : $"(set, {value.Length} chars)";

    /// <summary>
    /// Reports whether a vault-sourced secret actually resolved, without echoing its value.
    ///
    /// <para>
    /// Resolution is checked against <see cref="IConfiguration"/>, which is where
    /// <c>AddAzureKeyVault</c> + <c>PrefixKeyVaultSecretManager</c> deposit vault secrets —
    /// so this answers the question the endpoint exists for ("did the managed identity pull
    /// this secret?") using the same source the <c>keyvault</c> health check reads. The
    /// previous indirection through a <c>KeyVaultSecretProbe</c> interface had exactly one
    /// implementation, which always returned null, making this branch permanently dead.
    /// </para>
    /// </summary>
    private static IResult HandleKeyVaultDiag(
        [FromServices] IConfiguration configuration,
        [FromQuery] string? secret = null)
    {
        // Always return a tiny shape — never echo raw secret values.
        var payload = new Dictionary<string, object?>
        {
            ["vaultConfigured"] = !string.IsNullOrWhiteSpace(configuration["KeyVault:Uri"]),
            ["vaultUri"] = MaskUri(configuration["KeyVault:Uri"]),
            ["secretsProbed"] = Array.Empty<object>()
        };

        if (!string.IsNullOrWhiteSpace(secret))
        {
            string? value = configuration[secret];
            payload["requestedSecret"] = secret;
            payload["found"] = !string.IsNullOrEmpty(value);
            // Length bucket only; no characters of a secret escape.
            payload["masked"] = Mask(value);
        }

        return Results.Ok(payload);
    }

    private static string? MaskUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        // Show only the host (host-only) — never the path or query
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : "(unparseable)";
    }
}

