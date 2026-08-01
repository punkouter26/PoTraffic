// filepath: src/PoTraffic.API/Features/Config/DiagnosticsEndpoints.cs
// CI/CD rule #9 — Post-deployment smoke validation:
//   • GET /health (liveness + readiness)
//   • GET /diag/keyvault (MASKED Key Vault secret retrieval — proves identity wiring
//     and never returns the raw secret value to the caller)
// Consolidated into the Config slice so all /diag* + /api/system diagnostics live together.

using Microsoft.AspNetCore.Mvc;

namespace PoTraffic.API.Features.Config;

public static class DiagnosticsEndpoints
{
    /// <summary>Key-name fragments that mark a configuration value as secret.</summary>
    private static readonly string[] SensitiveKeyFragments =
        ["Key", "Secret", "Password", "Token", "ConnectionString", "ApiKey", "Credential"];

    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        // Every /diag* surface is admin-only and returns JSON. Rendering diagnostics as
        // JSON rather than hand-built HTML keeps configuration values — which are
        // attacker-influenceable in principle — out of an HTML sink entirely.
        RouteGroupBuilder group = app.MapGroup("/diag").WithTags("Diagnostics").RequireAuthorization("AdminOnly");

        // /diag/keyvault — only when ?secret= is supplied AND caller is admin
        // (otherwise just lists the secret NAMES the identity can see).
        group.MapGet("/keyvault", HandleKeyVaultDiag);

        // /diag/config — flattened IConfiguration with every secret-looking value masked.
        group.MapGet("/config", HandleConfigDiag);
    }

    /// <summary>
    /// Lists the resolved configuration so a misconfigured deployment can be diagnosed
    /// without shell access. Values whose key looks sensitive are masked and never
    /// returned in full (Rule 3 — "/diag must strictly mask secret values").
    /// </summary>
    private static IResult HandleConfigDiag(
        [FromServices] IConfiguration configuration,
        [FromServices] IWebHostEnvironment environment)
    {
        List<DiagConfigEntry> entries = [];
        foreach (IConfigurationSection section in configuration.GetChildren())
        {
            foreach (IConfigurationSection key in section.GetChildren())
            {
                // Match on the FULL path, not just the leaf. "ConnectionStrings:TableStorage"
                // is a secret even though the leaf ("TableStorage") looks innocuous — testing
                // the leaf alone would publish every connection string in the file.
                string path = $"{section.Key}:{key.Key}";
                bool isSensitive = Array.Exists(
                    SensitiveKeyFragments,
                    fragment => path.Contains(fragment, StringComparison.OrdinalIgnoreCase));

                entries.Add(new DiagConfigEntry(
                    Path: path,
                    Value: isSensitive ? Mask(key.Value) : key.Value ?? "(empty)",
                    IsSensitive: isSensitive));
            }
        }

        return Results.Ok(new
        {
            environment = environment.EnvironmentName,
            applicationName = environment.ApplicationName,
            entries = entries.OrderBy(e => e.Path).ToList(),
        });
    }

    /// <summary>
    /// Reveals only the length bucket of a secret — never any of its characters. Showing
    /// a prefix would leak the discriminating part of most API keys.
    /// </summary>
    private static string Mask(string? value)
        => string.IsNullOrEmpty(value) ? "(empty)" : $"(set, {value.Length} chars)";

    private static async Task<IResult> HandleKeyVaultDiag(
        [FromServices] IConfiguration configuration,
        [FromServices] KeyVaultSecretProbe probe,
        [FromQuery] string? secret = null,
        CancellationToken ct = default)
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
            try
            {
                string? value = await probe.TryGetAsync(secret, ct);
                payload["requestedSecret"] = secret;
                payload["found"] = value is not null;
                payload["length"] = value?.Length ?? 0;
                // Intentionally DO NOT echo the raw value.
                payload["maskedPreview"] = value is null
                    ? null
                    : string.Concat(value.AsSpan(0, Math.Min(2, value.Length)), new string('*', Math.Max(0, value.Length - 2)));
            }
            catch (Exception ex)
            {
                payload["error"] = ex.GetType().Name;
            }
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

/// <summary>One flattened configuration entry, with its value already masked if sensitive.</summary>
internal sealed record DiagConfigEntry(string Path, string Value, bool IsSensitive);

/// <summary>
/// Thin wrapper that tries to fetch a secret from Key Vault using the
/// shared managed identity. Returns <c>null</c> on any failure (the
/// /diag endpoint never throws — it always reports diagnostics).
/// </summary>
public interface KeyVaultSecretProbe
{
    Task<string?> TryGetAsync(string secretName, CancellationToken ct);
}

internal sealed class KeyVaultSecretProbeNoOp : KeyVaultSecretProbe
{
    public Task<string?> TryGetAsync(string secretName, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}