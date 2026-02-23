using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoTraffic.Client.Infrastructure.Http;

/// <summary>
/// Base class for all typed HTTP API clients in the Blazor WASM application.
/// Provides shared authentication header mutation and JSON-aware request helpers.
/// </summary>
public abstract class ApiClientBase
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    protected HttpClient HttpClient { get; }

    protected ApiClientBase(HttpClient httpClient)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>Sets the <c>Authorization: Bearer {token}</c> header on the shared HttpClient.</summary>
    protected void SetBearerToken(string token)
    {
        HttpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>Issues a GET request and deserialises the JSON body to <typeparamref name="T"/>.</summary>
    protected async Task<T> GetAsync<T>(string url, CancellationToken ct = default)
    {
        HttpResponseMessage response = await HttpClient.GetAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
        T result = await response.Content.ReadFromJsonAsync<T>(s_jsonOptions, ct)
                   ?? throw new InvalidOperationException($"GET {url} returned a null body.");
        return result;
    }

    /// <summary>Issues a POST request and deserialises the JSON response body to <typeparamref name="TResponse"/>.</summary>
    protected async Task<TResponse> PostAsync<TRequest, TResponse>(
        string url,
        TRequest body,
        CancellationToken ct = default)
    {
        HttpResponseMessage response = await HttpClient.PostAsJsonAsync(url, body, s_jsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        TResponse result = await response.Content.ReadFromJsonAsync<TResponse>(s_jsonOptions, ct)
                           ?? throw new InvalidOperationException($"POST {url} returned a null body.");
        return result;
    }

    /// <summary>Issues a PUT request with a JSON body. Expects a 2xx response with no body.</summary>
    protected async Task PutAsync<TRequest>(string url, TRequest body, CancellationToken ct = default)
    {
        HttpResponseMessage response = await HttpClient.PutAsJsonAsync(url, body, s_jsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>Issues a DELETE request. Expects a 2xx response.</summary>
    protected async Task DeleteAsync(string url, CancellationToken ct = default)
    {
        HttpResponseMessage response = await HttpClient.DeleteAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Minimal local projection of RFC 7807 ProblemDetails — avoids a server-side assembly reference
    private sealed record ProblemDetailsSlim(
        [property: JsonPropertyName("title")]  string? Title,
        [property: JsonPropertyName("detail")] string? Detail);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        // Attempt to extract a ProblemDetails title/detail before throwing
        string? detail = null;
        try
        {
            ProblemDetailsSlim? problem =
                await response.Content.ReadFromJsonAsync<ProblemDetailsSlim>(s_jsonOptions, ct);
            detail = problem?.Detail ?? problem?.Title;
        }
        catch { /* ignore deserialisation failure — fall through to generic message */ }

        string message = detail
            ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} from {response.RequestMessage?.RequestUri}";

        throw new HttpRequestException(message, inner: null, statusCode: response.StatusCode);
    }
}
