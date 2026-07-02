namespace PoTraffic.E2EAPI;

/// <summary>
/// Conditional Fact — runs when a Testing-environment app instance is reachable
/// at <c>E2E_BASE_URL</c> (default http://localhost:5150); skips gracefully otherwise.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SkipUnlessApiReadyAttribute : FactAttribute
{
    public static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://localhost:5150";

    private static readonly Lazy<bool> s_reachable = new(() =>
    {
        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
            using HttpResponseMessage health = http.GetAsync($"{BaseUrl}/health").GetAwaiter().GetResult();
            if (!health.IsSuccessStatusCode) return false;
            // Testing endpoints must be present — never run against Dev/Prod.
            using HttpResponseMessage seed = http.PostAsync($"{BaseUrl}/e2e/seed-admin", content: null).GetAwaiter().GetResult();
            return seed.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    });

    public SkipUnlessApiReadyAttribute()
    {
        if (!s_reachable.Value)
            Skip = $"App not reachable at {BaseUrl} — start the API with ASPNETCORE_ENVIRONMENT=Testing (or set E2E_BASE_URL).";
    }
}
