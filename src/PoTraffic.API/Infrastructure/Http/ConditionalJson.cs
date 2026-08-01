using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Http.Headers;
using Microsoft.Net.Http.Headers;

namespace PoTraffic.API.Infrastructure.Http;

/// <summary>
/// JSON results that carry a content-derived <c>ETag</c> and answer <c>304 Not Modified</c>
/// when the caller already has that exact payload.
///
/// <para>
/// The client polls these endpoints on a timer, and most ticks return byte-identical data:
/// a route's baseline changes only when a new sample lands, and its weekday comparison
/// barely moves at all. Revalidating costs a request either way, but a 304 costs a header
/// exchange instead of a full body — which on the dashboard's <c>2 + 2N</c> requests per
/// tick is the difference between kilobytes and hundreds of them.
/// </para>
///
/// <para>
/// Responses are marked <c>private, no-cache</c>: these are per-user, so no shared cache may
/// store them, and the browser must revalidate every time rather than serving a stale body.
/// </para>
/// </summary>
public static class ConditionalJson
{
    // Matches the minimal-API default (JsonSerializerDefaults.Web), so a payload
    // produced here is byte-identical to the same value returned via Results.Ok.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Serialises <paramref name="value"/>, tags it with a strong ETag, and returns either the
    /// body or <c>304</c> when the request's <c>If-None-Match</c> already matches.
    /// </summary>
    public static IResult Ok<T>(HttpContext ctx, T value)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        string etag = BuildETag(payload);

        ctx.Response.Headers.ETag = etag;
        ctx.Response.Headers.CacheControl = "private, no-cache";

        return IfNoneMatch(ctx, etag)
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.Bytes(payload, "application/json");
    }

    /// <summary>Strong validator over the exact bytes on the wire.</summary>
    private static string BuildETag(byte[] payload) =>
        $"\"{Convert.ToHexString(SHA256.HashData(payload))[..32]}\"";

    /// <summary>
    /// True when the caller already holds this representation. Handles the multi-value and
    /// wildcard forms of the header rather than a bare string comparison.
    /// </summary>
    private static bool IfNoneMatch(HttpContext ctx, string etag)
    {
        RequestHeaders headers = ctx.Request.GetTypedHeaders();
        IList<EntityTagHeaderValue> candidates = headers.IfNoneMatch;

        if (candidates is null || candidates.Count == 0)
            return false;

        EntityTagHeaderValue current = new(etag);
        return candidates.Any(c =>
            c.Equals(EntityTagHeaderValue.Any) || c.Compare(current, useStrongComparison: true));
    }
}
