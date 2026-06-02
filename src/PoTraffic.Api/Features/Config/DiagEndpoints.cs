using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Api.Infrastructure.Providers;
using System.Security.Cryptography;

namespace PoTraffic.Api.Features.Config;

/// <summary>
/// Diagnostics page showing status of all external connections, keys, and configuration.
/// Masked sensitive values for security.
///
/// Rule 4 — /diag must be available in both Development and Production. In
/// Production the route is "hidden" (unobvious path + secret token header) so
/// that it doesn't appear in UI navigation. The secret token is configured via
/// <c>Diag:SecretToken</c> in appsettings (overridable via Key Vault).
/// </summary>
public static class DiagEndpoints
{
    private const string SecretHeaderName = "X-PoTraffic-Diag";

    public static IEndpointRouteBuilder MapDiagEndpoints(this IEndpointRouteBuilder app)
    {
        // /diag-{env}?key={secret} — Production-safe hidden diagnostic page.
        // The path segment is unpredictable (per-environment) and the secret is
        // checked before any data is rendered.
        app.MapGet("/diag-{envSegment}", async (
            string envSegment,
            HealthCheckService healthCheckService,
            IConfiguration configuration,
            IWebHostEnvironment env,
            HttpContext httpContext,
            ILogger<Program> logger) =>
        {
            // Dev: any /diag-* URL is allowed (no secret required) for convenience.
            // Non-Dev: require header X-PoTraffic-Diag matching the configured secret.
            if (!env.IsDevelopment())
            {
                string? expected = configuration["Diag:SecretToken"];
                string? presented = httpContext.Request.Headers[SecretHeaderName].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(expected) ||
                    !CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(expected),
                        System.Text.Encoding.UTF8.GetBytes(presented ?? string.Empty)))
                {
                    return (IResult)Results.NotFound();
                }
            }

            // Get health check results
            HealthReport report = await healthCheckService.CheckHealthAsync();

            // Build connection status
            var connections = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description ?? "",
                durationMs = e.Value.Duration.TotalMilliseconds,
                exception = e.Value.Exception?.Message ?? ""
            }).ToList();

            // Build masked configuration keys (non-sensitive only)
            var configKeys = new List<object>();
            var sensitivePatterns = new[] { "Key", "Secret", "Password", "Token", "ConnectionString", "ApiKey" };

            foreach (var section in configuration.GetChildren())
            {
                foreach (var key in section.GetChildren())
                {
                    bool isSensitive = sensitivePatterns.Any(p =>
                        key.Key.Contains(p, StringComparison.OrdinalIgnoreCase));

                    configKeys.Add(new
                    {
                        path = $"{section.Key}:{key.Key}",
                        value = isSensitive ? MaskValue(key.Value ?? "") : key.Value ?? "(empty)",
                        isSensitive
                    });
                }
            }

            // Build environment info
            var envInfo = new
            {
                environment = env.EnvironmentName,
                applicationName = env.ApplicationName,
                contentRootPath = env.ContentRootPath,
                isDevelopment = env.IsDevelopment(),
                isProduction = env.IsProduction(),
                isTesting = env.IsEnvironment("Testing")
            };

            string html = GenerateDiagHtml(connections.Cast<object>().ToList(), configKeys, envInfo, report.Status);
            return (IResult)Results.Content(html, "text/html");
        })
        .ExcludeFromDescription()
        .AllowAnonymous();

        return app;
    }

    private static string MaskValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        if (value.Length <= 4) return "****";

        int visibleChars = Math.Min(3, value.Length / 4);
        return value[..visibleChars] + new string('*', Math.Max(4, value.Length - visibleChars * 2)) + value[^visibleChars..];
    }

    private static string GenerateDiagHtml(
        List<object> connections,
        List<object> configKeys,
        object envInfo,
        HealthStatus overallStatus)
    {
        dynamic env = envInfo;
        string statusColor = overallStatus switch
        {
            HealthStatus.Healthy => "#22c55e",
            HealthStatus.Degraded => "#f59e0b",
            _ => "#ef4444"
        };

        return $@"
<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>PoTraffic Diagnostics</title>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #0f172a; color: #e2e8f0; min-height: 100vh; padding: 2rem; }}
        .container {{ max-width: 1200px; margin: 0 auto; }}
        h1 {{ color: #f8fafc; margin-bottom: 0.5rem; }}
        .subtitle {{ color: #94a3b8; margin-bottom: 2rem; }}
        .card {{ background: #1e293b; border-radius: 12px; padding: 1.5rem; margin-bottom: 1.5rem; border: 1px solid #334155; }}
        .card h2 {{ color: #f8fafc; font-size: 1.25rem; margin-bottom: 1rem; border-bottom: 1px solid #334155; padding-bottom: 0.5rem; }}
        .status-badge {{ display: inline-block; padding: 0.25rem 0.75rem; border-radius: 9999px; font-size: 0.875rem; font-weight: 600; }}
        .status-healthy {{ background: rgba(34, 197, 94, 0.2); color: #22c55e; }}
        .status-degraded {{ background: rgba(245, 158, 11, 0.2); color: #f59e0b; }}
        .status-unhealthy {{ background: rgba(239, 68, 68, 0.2); color: #ef4444; }}
        table {{ width: 100%; border-collapse: collapse; }}
        th, td {{ text-align: left; padding: 0.75rem; border-bottom: 1px solid #334155; }}
        th {{ color: #94a3b8; font-weight: 500; font-size: 0.875rem; }}
        tr:last-child td {{ border-bottom: none; }}
        .status-dot {{ display: inline-block; width: 8px; height: 8px; border-radius: 50%; margin-right: 0.5rem; }}
        .dot-healthy {{ background: #22c55e; }}
        .dot-degraded {{ background: #f59e0b; }}
        .dot-unhealthy {{ background: #ef4444; }}
        .sensitive {{ color: #f59e0b; font-family: monospace; }}
        .env-grid {{ display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 1rem; }}
        .env-item {{ background: #334155; padding: 1rem; border-radius: 8px; }}
        .env-item label {{ display: block; color: #94a3b8; font-size: 0.75rem; margin-bottom: 0.25rem; }}
        .env-item value {{ display: block; font-family: monospace; }}
        .timestamp {{ color: #64748b; font-size: 0.875rem; margin-top: 2rem; text-align: center; }}
    </style>
</head>
<body>
    <div class=""container"">
        <h1>🔧 PoTraffic Diagnostics</h1>
        <p class=""subtitle"">Development diagnostics page — connection status and configuration</p>
        
        <div class=""card"">
            <h2>Overall Status</h2>
            <span class=""status-badge status-{overallStatus.ToString().ToLower()}"">{overallStatus}</span>
        </div>
        
        <div class=""card"">
            <h2>External Connections</h2>
            <table>
                <thead>
                    <tr>
                        <th>Connection</th>
                        <th>Status</th>
                        <th>Duration (ms)</th>
                        <th>Details</th>
                    </tr>
                </thead>
                <tbody>
                    {string.Join("\n", connections.Select(c =>
        {
            dynamic conn = c;
            string dotClass = conn.status == "Healthy" ? "dot-healthy" : conn.status == "Degraded" ? "dot-degraded" : "dot-unhealthy";
            return $@"<tr>
                            <td><span class=""status-dot {dotClass}""></span>{conn.name}</td>
                            <td><span class=""status-badge status-{conn.status.ToString().ToLower()}"">{conn.status}</span></td>
                            <td>{conn.durationMs:F2}</td>
                            <td style=""color: #94a3b8; font-size: 0.875rem;"">{conn.description} {conn.exception}</td>
                        </tr>";
        }))}
                </tbody>
            </table>
        </div>
        
        <div class=""card"">
            <h2>Configuration Keys</h2>
            <table>
                <thead>
                    <tr>
                        <th>Key</th>
                        <th>Value</th>
                    </tr>
                </thead>
                <tbody>
                    {string.Join("\n", configKeys.Select(c =>
        {
            dynamic cfg = c;
            string valueClass = cfg.isSensitive ? "sensitive" : "";
            return $@"<tr>
                            <td>{cfg.path}</td>
                            <td class=""{valueClass}"">{cfg.value}</td>
                        </tr>";
        }))}
                </tbody>
            </table>
        </div>
        
        <div class=""card"">
            <h2>Environment</h2>
            <div class=""env-grid"">
                <div class=""env-item"">
                    <label>Environment</label>
                    <value>{env.environment}</value>
                </div>
                <div class=""env-item"">
                    <label>Application Name</label>
                    <value>{env.applicationName}</value>
                </div>
                <div class=""env-item"">
                    <label>Development</label>
                    <value>{env.isDevelopment}</value>
                </div>
                <div class=""env-item"">
                    <label>Production</label>
                    <value>{env.isProduction}</value>
                </div>
                <div class=""env-item"">
                    <label>Testing</label>
                    <value>{env.isTesting}</value>
                </div>
            </div>
        </div>
        
        <p class=""timestamp"">Generated at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>
    </div>
</body>
</html>";
    }
}
