using System.Globalization;
using FluentAssertions;
using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.API.Infrastructure.Testing;

namespace PoTraffic.UnitTests.Infrastructure;

/// <summary>
/// The mock geocoder used to map every address onto one shared point, which made any two
/// addresses look like the same location and got route creation rejected with
/// SAME_COORDINATES. These tests pin the two properties that failure depended on.
/// </summary>
public sealed class MockTrafficProviderTests
{
    private static readonly MockTrafficProvider Provider = new();

    private static async Task<(double Lat, double Lon)> GeocodeAsync(string address)
    {
        string? raw = await Provider.GeocodeAsync(address);
        raw.Should().NotBeNull();

        string[] parts = raw!.Split(',');
        parts.Should().HaveCount(2, "coordinates must be a parseable 'lat,lon' pair");

        return (double.Parse(parts[0], CultureInfo.InvariantCulture),
                double.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("1600 Amphitheatre Parkway, Mountain View, CA", "1 Infinite Loop, Cupertino, CA")]
    [InlineData("A", "C")]
    [InlineData("Origin", "Destination")]
    [InlineData("501 Sylview Dr, Pasadena, CA", "456 S Fair Oaks Ave, Pasadena, CA")]
    public async Task DifferentAddresses_ResolveToDifferentCoordinates(string origin, string destination)
    {
        string? originCoords = await Provider.GeocodeAsync(origin);
        string? destinationCoords = await Provider.GeocodeAsync(destination);

        originCoords.Should().NotBe(destinationCoords,
            "identical coordinates make CreateRouteCommand reject the pair as SAME_COORDINATES");
    }

    [Fact]
    public async Task SameAddress_ResolvesToTheSameCoordinatesEveryTime()
    {
        string? first = await Provider.GeocodeAsync("501 Sylview Dr, Pasadena, CA");
        string? second = await Provider.GeocodeAsync("  501 SYLVIEW DR, PASADENA, CA  ");

        second.Should().Be(first, "lookups must be stable across calls, casing and surrounding space");
    }

    [Fact]
    public async Task Coordinates_AreWithinAPlausibleGeographicRange()
    {
        (double lat, double lon) = await GeocodeAsync("somewhere in particular");

        lat.Should().BeInRange(-90, 90);
        lon.Should().BeInRange(-180, 180);
    }

    [Fact]
    public async Task ManyDistinctAddresses_ProduceNoCollisions()
    {
        string[] addresses = [.. Enumerable.Range(0, 250).Select(i => $"{i} Test Street")];

        string?[] coordinates = await Task.WhenAll(addresses.Select(a => Provider.GeocodeAsync(a)));

        coordinates.Distinct().Should().HaveCount(addresses.Length,
            "a seeded batch of routes must not collapse onto shared points");
    }

    [Fact]
    public async Task GetTravelTime_ReturnsPlausibleValues_UnderConcurrency()
    {
        // Guards the Random.Shared switch: a shared non-thread-safe Random degrades to
        // returning zeros when hit concurrently, which would silently produce 0s travel times.
        TravelResult?[] results = await Task.WhenAll(
            Enumerable.Range(0, 100).Select(_ => Provider.GetTravelTimeAsync("1,1", "2,2")));

        results.Should().AllSatisfy(r =>
        {
            r.Should().NotBeNull();
            r!.DurationSeconds.Should().BeInRange(900, 2700);
            r.DistanceMetres.Should().BeInRange(5000, 15000);
        });
    }
}
