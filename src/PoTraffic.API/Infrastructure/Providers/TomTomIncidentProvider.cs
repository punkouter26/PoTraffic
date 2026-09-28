using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PoTraffic.API.Infrastructure.Providers;

/// <summary>Explains a slowdown: the worst live incident on a route's road shape.</summary>
public interface IIncidentProvider
{
    /// <summary>
    /// The worst incident on the route, or null when there is nothing on it, no API key,
    /// or the lookup failed.
    /// </summary>
    Task<RouteIncident?> FindWorstOnPathAsync(string encodedPolyline, CancellationToken ct = default);
}

/// <summary>An incident on a route's road shape.</summary>
/// <param name="Description">"Stationary traffic on I-5 near Exit 164, adding about 12 min".</param>
/// <param name="Lat">The incident's point nearest the route.</param>
/// <param name="Lon">The incident's point nearest the route.</param>
/// <param name="SeenAt">When the lookup found it; the map stops showing it once it is stale.</param>
public sealed record RouteIncident(string Description, double Lat, double Lon, DateTimeOffset SeenAt);

/// <summary>
/// TomTom Traffic Incident Details (v5). Needs <c>TomTom:ApiKey</c>; without one it is a
/// no-op, so the feature stays dark rather than failing. Only consulted when an alert fires,
/// which keeps it far inside the free tier (2,500 requests/day).
/// </summary>
public sealed class TomTomIncidentProvider(HttpClient http, IConfiguration config, ILogger<TomTomIncidentProvider> logger)
    : IIncidentProvider
{
    private const string Fields =
        "{incidents{geometry{type,coordinates},properties{magnitudeOfDelay,events{description},from,roadNumbers,delay}}}";

    /// <summary>How far off the road shape an incident may sit and still count as on the route.</summary>
    internal const double OnRouteMetres = 150;

    public async Task<RouteIncident?> FindWorstOnPathAsync(string encodedPolyline, CancellationToken ct = default)
    {
        string? key = config["TomTom:ApiKey"];
        List<(double Lat, double Lon)> path = DecodePolyline(encodedPolyline);
        if (string.IsNullOrWhiteSpace(key) || path.Count < 2)
            return null;

        const double Pad = 0.005; // ~500 m, so incidents at the ends of the route are inside the box
        string bbox = string.Join(',',
            new[] { path.Min(p => p.Lon) - Pad, path.Min(p => p.Lat) - Pad, path.Max(p => p.Lon) + Pad, path.Max(p => p.Lat) + Pad }
                .Select(v => v.ToString("F5", CultureInfo.InvariantCulture)));
        string url = "https://api.tomtom.com/traffic/services/5/incidentDetails"
            + $"?key={Uri.EscapeDataString(key)}&bbox={bbox}&fields={Uri.EscapeDataString(Fields)}"
            + "&language=en-US&timeValidityFilter=present";

        try
        {
            using HttpResponseMessage resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("TomTom incidents returned {Status}", (int)resp.StatusCode);
                return null;
            }

            using JsonDocument doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return Worst(doc.RootElement, path);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "TomTom incident lookup failed");
            return null;
        }
    }

    /// <summary>The most severe incident within <see cref="OnRouteMetres"/> of the path, described.</summary>
    internal static RouteIncident? Worst(JsonElement root, List<(double Lat, double Lon)> path)
    {
        if (!root.TryGetProperty("incidents", out JsonElement incidents))
            return null;

        JsonElement? worst = null;
        (double Lat, double Lon) worstAt = default;
        (int Magnitude, int Delay) worstRank = (-1, -1);
        foreach (JsonElement incident in incidents.EnumerateArray())
        {
            (double Lat, double Lon) nearest = default;
            double nearestMetres = double.MaxValue;
            foreach ((double Lat, double Lon) c in Coordinates(incident))
            {
                double m = DistanceToPathMetres(c, path);
                if (m < nearestMetres)
                    (nearest, nearestMetres) = (c, m);
            }
            if (nearestMetres > OnRouteMetres)
                continue;

            JsonElement props = incident.GetProperty("properties");
            (int, int) rank = (Int(props, "magnitudeOfDelay"), Int(props, "delay"));
            if (rank.CompareTo(worstRank) > 0)
                (worst, worstAt, worstRank) = (props, nearest, rank);
        }

        if (worst is not { } p)
            return null;

        string what = p.TryGetProperty("events", out JsonElement events) && events.GetArrayLength() > 0
            && events[0].TryGetProperty("description", out JsonElement d) && d.GetString() is { Length: > 0 } desc
            ? desc
            : "Traffic incident";
        string road = p.TryGetProperty("roadNumbers", out JsonElement roads) && roads.GetArrayLength() > 0
            ? $" on {roads[0].GetString()}" : "";
        string near = p.TryGetProperty("from", out JsonElement from) && from.GetString() is { Length: > 0 } f
            ? $" near {f}" : "";
        int delayMin = (int)Math.Round(worstRank.Delay / 60.0);
        string delay = delayMin > 0 ? $", adding about {delayMin} min" : "";
        return new RouteIncident(what + road + near + delay, worstAt.Lat, worstAt.Lon, DateTimeOffset.UtcNow);
    }

    private static int Int(JsonElement props, string name) =>
        props.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int i) ? i : 0;

    /// <summary>Point → [lon, lat]; LineString → [[lon, lat], …].</summary>
    private static IEnumerable<(double Lat, double Lon)> Coordinates(JsonElement incident)
    {
        if (!incident.TryGetProperty("geometry", out JsonElement g) || !g.TryGetProperty("coordinates", out JsonElement c))
            yield break;

        if (c.GetArrayLength() > 0 && c[0].ValueKind == JsonValueKind.Number)
        {
            yield return (c[1].GetDouble(), c[0].GetDouble());
            yield break;
        }
        foreach (JsonElement pt in c.EnumerateArray())
            yield return (pt[1].GetDouble(), pt[0].GetDouble());
    }

    /// <summary>
    /// Distance from a point to the nearest segment of the path, on a local flat projection —
    /// exact enough at commute scale, where a 150 m tolerance dwarfs the projection error.
    /// </summary>
    internal static double DistanceToPathMetres((double Lat, double Lon) point, List<(double Lat, double Lon)> path)
    {
        double kx = 111_320 * Math.Cos(point.Lat * Math.PI / 180);
        const double ky = 110_540;
        double best = double.MaxValue;
        for (int i = 1; i < path.Count; i++)
        {
            double ax = (path[i - 1].Lon - point.Lon) * kx, ay = (path[i - 1].Lat - point.Lat) * ky;
            double bx = (path[i].Lon - point.Lon) * kx, by = (path[i].Lat - point.Lat) * ky;
            double dx = bx - ax, dy = by - ay;
            double lengthSq = dx * dx + dy * dy;
            double t = lengthSq == 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / lengthSq, 0, 1);
            double px = ax + t * dx, py = ay + t * dy;
            best = Math.Min(best, Math.Sqrt(px * px + py * py));
        }
        return best;
    }

    /// <summary>Google's encoded polyline algorithm, precision 5 (inverse of CreateSampleRouteCommand.EncodePolyline).</summary>
    internal static List<(double Lat, double Lon)> DecodePolyline(string encoded)
    {
        List<(double, double)> points = [];
        int index = 0, lat = 0, lon = 0;
        while (index < encoded.Length)
        {
            lat += Next();
            lon += Next();
            points.Add((lat / 1e5, lon / 1e5));
        }
        return points;

        int Next()
        {
            int result = 0, shift = 0, b;
            do
            {
                if (index >= encoded.Length) return 0;
                b = encoded[index++] - 63;
                result |= (b & 0x1f) << shift;
                shift += 5;
            } while (b >= 0x20);
            return (result & 1) != 0 ? ~(result >> 1) : result >> 1;
        }
    }
}
