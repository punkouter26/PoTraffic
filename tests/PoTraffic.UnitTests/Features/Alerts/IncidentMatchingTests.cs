using System.Text.Json;
using FluentAssertions;
using PoTraffic.API.Features.Routes;
using PoTraffic.API.Infrastructure.Providers;
using Xunit;

namespace PoTraffic.UnitTests.Features.Alerts;

/// <summary>Only incidents on the route's road shape explain an alert — not every crash in the bounding box.</summary>
public sealed class IncidentMatchingTests
{
    // A straight north-south road, ~11 km long.
    private static readonly List<(double Lat, double Lon)> Path = [(47.60, -122.33), (47.70, -122.33)];

    [Fact]
    public void Polyline_RoundTrips()
    {
        string encoded = CreateSampleRouteCommandHandler.EncodePolyline(Path);
        TomTomIncidentProvider.DecodePolyline(encoded).Should().Equal(Path);
    }

    [Fact]
    public void Worst_IgnoresIncidentsOffTheRoute_AndPicksTheMostSevereOnIt()
    {
        string json = """
        { "incidents": [
          { "geometry": { "type": "Point", "coordinates": [-122.30, 47.65] },
            "properties": { "magnitudeOfDelay": 4, "delay": 1800, "events": [{ "description": "Road closed" }] } },
          { "geometry": { "type": "LineString", "coordinates": [[-122.3301, 47.64], [-122.3301, 47.645]] },
            "properties": { "magnitudeOfDelay": 3, "delay": 720, "roadNumbers": ["I-5"], "from": "Exit 164",
                            "events": [{ "description": "Stationary traffic" }] } },
          { "geometry": { "type": "Point", "coordinates": [-122.33, 47.66] },
            "properties": { "magnitudeOfDelay": 1, "delay": 60, "events": [{ "description": "Slow traffic" }] } }
        ] }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);

        // The road closure is ~2 km east of the route, so it must not be blamed.
        RouteIncident? worst = TomTomIncidentProvider.Worst(doc.RootElement, Path);
        worst!.Description.Should().Be("Stationary traffic on I-5 near Exit 164, adding about 12 min");

        // Located at the incident's own point nearest the road, not the bounding box.
        (worst.Lat, worst.Lon).Should().Be((47.64, -122.3301));
    }
}
