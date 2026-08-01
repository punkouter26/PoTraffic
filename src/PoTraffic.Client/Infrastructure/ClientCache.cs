using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.JSInterop;

namespace PoTraffic.Client.Infrastructure;

/// <summary>
/// Last-known-good payload store backed by <c>localStorage</c>, so a page can paint
/// real data on mount instead of skeletons while the first round-trip is still in
/// flight. Reads are stale-by-design: callers render what they get, then reconcile
/// when the network response lands (cache-then-revalidate).
///
/// <para>
/// Entries are namespaced per user. A shared browser must not show one account's
/// routes to the next — <see cref="UseScope"/> is called with the signed-in user's
/// identifier, and everything written afterwards lands under that prefix.
/// </para>
/// </summary>
public sealed class ClientCache
{
    private const string KeyPrefix = "pt-cache";

    /// <summary>
    /// Service worker caches holding per-user API responses. Must match the names in
    /// push-sw.js; the shell cache is deliberately excluded, as it holds only static assets.
    /// </summary>
    private static readonly string[] ServiceWorkerDataCaches = ["pt-data-v1"];

    private readonly IJSRuntime _js;
    private string _scope = "anon";

    public ClientCache(IJSRuntime js) => _js = js;

    /// <summary>Raised after <see cref="SetAsync"/> so listeners can react to a fresh snapshot.</summary>
    public event Action<string>? Written;

    /// <summary>
    /// Namespaces every subsequent read and write. Called once the authenticated
    /// identity is known; a different scope simply misses rather than leaking.
    /// </summary>
    public void UseScope(string scope) =>
        _scope = string.IsNullOrWhiteSpace(scope) ? "anon" : scope;

    /// <summary>
    /// The cached value for <paramref name="key"/>, or <c>null</c> when absent,
    /// unparseable, or older than <paramref name="maxAge"/>. Never throws: a
    /// storage failure (private mode, quota, a stale schema) is a cache miss.
    /// </summary>
    public async ValueTask<T?> GetAsync<T>(string key, JsonTypeInfo<T> typeInfo, TimeSpan maxAge)
        where T : class
    {
        try
        {
            string? raw = await _js.InvokeAsync<string?>("localStorage.getItem", Key(key));
            if (string.IsNullOrEmpty(raw))
                return null;

            int split = raw.IndexOf('|');
            if (split <= 0 || !long.TryParse(raw[..split], out long storedAtUnixMs))
                return null;

            DateTimeOffset storedAt = DateTimeOffset.FromUnixTimeMilliseconds(storedAtUnixMs);
            if (DateTimeOffset.UtcNow - storedAt > maxAge)
                return null;

            return JsonSerializer.Deserialize(raw[(split + 1)..], typeInfo);
        }
        catch
        {
            // A cache that fails is a cache that misses — never a page that breaks.
            return null;
        }
    }

    /// <summary>Stores <paramref name="value"/> stamped with the current time.</summary>
    public async ValueTask SetAsync<T>(string key, T value, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            string payload =
                $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}|{JsonSerializer.Serialize(value, typeInfo)}";
            await _js.InvokeVoidAsync("localStorage.setItem", Key(key), payload);
            Written?.Invoke(key);
        }
        catch
        {
            // Quota exceeded or storage disabled — the app works, it just starts cold.
        }
    }

    /// <summary>
    /// Drops every cached entry for every scope, and the service worker's cached API
    /// responses with them. Called on sign-out: leaving either behind would let the next
    /// account on a shared browser page through the previous one's data.
    /// </summary>
    public async ValueTask ClearAllAsync()
    {
        _scope = "anon";

        try
        {
            IJSObjectReference module =
                await _js.InvokeAsync<IJSObjectReference>("import", "./js/pt-cache.js");
            await using (module.ConfigureAwait(false))
            {
                await module.InvokeAsync<int>(
                    "clearUserData", $"{KeyPrefix}:", ServiceWorkerDataCaches);
            }
        }
        catch
        {
            // Best effort — a cache we cannot clear is not a reason to block sign-out.
        }
    }

    private string Key(string key) => $"{KeyPrefix}:{_scope}:{key}";
}
