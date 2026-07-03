// filepath: src/PoTraffic.Api/Features/Diagnostics/DiagnosticsEndpoints.cs
// CI/CD rule #9 — Post-deployment smoke validation:
//   • GET /health (liveness + readiness)
//   • GET /diag/keyvault (MASKED Key Vault secret retrieval — proves identity wiring
//     and never returns the raw secret value to the caller)

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using PoTraffic.Api.Infrastructure.Security;

namespace PoTraffic.Api.Features.Diagnostics;

public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        // /diag/keyvault — only when ?secret= is supplied AND caller is admin
        // (otherwise just lists the secret NAMES the identity can see).
        RouteGroupBuilder group = app.MapGroup("/diag").WithTags("Diagnostics").RequireAuthorization("AdminOnly");
        group.MapGet("/keyvault", HandleKeyVaultDiag);
    }

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