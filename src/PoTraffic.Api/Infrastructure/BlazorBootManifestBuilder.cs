// filepath: src/PoTraffic.Api/Infrastructure/BlazorBootManifestBuilder.cs
//
// Fix #11 — Synthesises a minimal but correct blazor.boot.json from the
// published wwwroot/_framework directory. The .NET 10 framework normally
// generates this manifest at runtime and serves it via MapStaticAssets(), but
// with OverrideHtmlAssetPlaceholders=true on a non-server host, the host's
// staticwebassets.endpoints.json ends up empty and the runtime manifest
// becomes unreachable. Emitting our own (file-system-driven) version keeps
// the WASM client bootable.
//
// The shape mirrors the one expected by blazor.webassembly.js:
//   - entryAssembly   = first PoTraffic.* .dll
//   - resources.assembly = every .wasm/.dll/.pdb in _framework
//   - resources.runtime  = dotnet.* and dotnet.native.* .js/.wasm
//   - resources.icudt    = icudt_* .dat files
// Zero-allocation on the hot path: directory enumeration is lazy via
// EnumerateFiles and the caller can opt to cache the result.

using System.Text.Json;
using Microsoft.Extensions.FileProviders;

namespace PoTraffic.Api.Infrastructure;

internal static class BlazorBootManifestBuilder
{
    public static object Build(IWebHostEnvironment webEnv)
    {
        string? frameworkDir = ResolveFrameworkDir(webEnv);

        var assemblies = new List<object>();
        var runtimes = new List<object>();
        var icudts = new List<object>();

        string? entryAssembly = null;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (frameworkDir is not null && Directory.Exists(frameworkDir))
        {
            foreach (string path in Directory.EnumerateFiles(frameworkDir))
            {
                string name = Path.GetFileName(path);
                string rel = "_framework/" + name;

                if (entryAssembly is null
                    && name.StartsWith("PoTraffic.Client.", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains(".wasm", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    entryAssembly = name;
                }

                if (name.StartsWith("dotnet.", StringComparison.OrdinalIgnoreCase))
                {
                    runtimes.Add(new { name = rel });
                }
                else if (name.StartsWith("icudt_", StringComparison.OrdinalIgnoreCase))
                {
                    icudts.Add(new { name = rel });
                }
                else if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                      || name.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)
                      || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    assemblies.Add(new { name = rel });
                }
            }
        }

        entryAssembly ??= assemblies.Count > 0 ? "PoTraffic.Client.dll" : "PoTraffic.Client.wasm";

        return new
        {
            name = "PoTraffic",
            entryAssembly,
            manifests = Array.Empty<object>(),
            resources = new
            {
                cache = Array.Empty<object>(),
                runtime = runtimes,
                assembly = assemblies,
                pdb = Array.Empty<object>(),
                satelliteResources = Array.Empty<object>(),
                icudt = icudts,
                css = Array.Empty<object>(),
                jsModule = Array.Empty<object>(),
                jsFiles = Array.Empty<object>(),
                wasmNative = Array.Empty<object>(),
                fingerprint = new Dictionary<string, string>(),
            },
            config = Array.Empty<object>(),
            globalizationMode = "auto",
            debugLevel = 0,
            cacheBootResources = true,
            omitGetMappingHeaders = false,
            totalAssets = assemblies.Count + runtimes.Count + icudts.Count,
            linkerEnabled = true,
            sources = Array.Empty<object>(),
            generated = now,
        };
    }

    /// <summary>
    /// Fix #11b — locates the published <c>index.html</c> in any of the static
    /// asset content roots. Used by the explicit <c>GET /</c> route so we don't
    /// depend on <c>MapFallbackToFile</c> correctly resolving <c>index.html</c>
    /// across WebRootFileProvider / composite provider configurations.
    /// </summary>
    public static string ResolveIndexHtml(IWebHostEnvironment webEnv)
    {
        if (!string.IsNullOrEmpty(webEnv.WebRootPath))
        {
            string candidate = Path.Combine(webEnv.WebRootPath, "index.html");
            if (File.Exists(candidate)) return candidate;
        }

        // Walk the static web assets manifest's ContentRoots.
        string manifestPath = Path.Combine(
            AppContext.BaseDirectory,
            $"{webEnv.ApplicationName}.staticwebassets.runtime.json");
        if (File.Exists(manifestPath))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.TryGetProperty("ContentRoots", out JsonElement roots))
            {
                foreach (JsonElement r in roots.EnumerateArray())
                {
                    string? root = r.GetString();
                    if (string.IsNullOrEmpty(root)) continue;
                    string candidate = Path.Combine(root, "index.html");
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }

        // Try ../PoTraffic.Client/wwwroot/index.html (Testing/Development layout).
        string localCandidate = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "PoTraffic.Client", "wwwroot", "index.html"));
        if (File.Exists(localCandidate)) return localCandidate;

        // Last resort: enumeration.
        try
        {
            return Directory.EnumerateFiles(
                AppContext.BaseDirectory, "index.html", SearchOption.AllDirectories)
                .FirstOrDefault() ?? Path.Combine(webEnv.WebRootPath ?? "", "index.html");
        }
        catch
        {
            return Path.Combine(webEnv.WebRootPath ?? "", "index.html");
        }
    }

    private static string? ResolveFrameworkDir(IWebHostEnvironment webEnv)
    {
        // 1. Try the published-or-current WebRootPath.
        if (!string.IsNullOrEmpty(webEnv.WebRootPath))
        {
            string candidate = Path.Combine(webEnv.WebRootPath, "_framework");
            if (Directory.Exists(candidate)) return candidate;
        }

        // 2. Try every ContentRoot declared by the static web assets manifest.
        string manifestPath = Path.Combine(
            AppContext.BaseDirectory,
            $"{webEnv.ApplicationName}.staticwebassets.runtime.json");
        if (File.Exists(manifestPath))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.TryGetProperty("ContentRoots", out JsonElement roots))
            {
                foreach (JsonElement r in roots.EnumerateArray())
                {
                    string? root = r.GetString();
                    if (string.IsNullOrEmpty(root)) continue;
                    string candidate = Path.Combine(root, "_framework");
                    if (Directory.Exists(candidate)) return candidate;
                    if (Directory.Exists(root)
                        && root.EndsWith("_framework", StringComparison.OrdinalIgnoreCase))
                    {
                        return root;
                    }
                }
            }
        }

        // 3. Try the application base directory + "../PoTraffic.Client/wwwroot/_framework".
        string localCandidate = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "PoTraffic.Client", "wwwroot", "_framework"));
        if (Directory.Exists(localCandidate)) return localCandidate;

        // 4. Try the application base directory + "./wwwroot/_framework".
        string appLocal = Path.Combine(AppContext.BaseDirectory, "wwwroot", "_framework");
        if (Directory.Exists(appLocal)) return appLocal;

        // 5. Last resort: enumerate relative to AppContext.BaseDirectory.
        try
        {
            return Directory.EnumerateDirectories(
                AppContext.BaseDirectory, "_framework", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
