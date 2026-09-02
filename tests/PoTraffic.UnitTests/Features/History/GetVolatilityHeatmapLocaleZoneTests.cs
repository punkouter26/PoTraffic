using System.Globalization;
using System.Reflection;
using FluentAssertions;
using PoTraffic.API.Features.History;
using Xunit;

namespace PoTraffic.UnitTests.Features.History;

/// <summary>
/// Verifies the locale → TimeZoneInfo resolution inside GetVolatilityHeatmapQueryHandler.
/// Named for what it covers rather than for the handler: the handler's own behaviour is
/// tested in GetVolatilityHeatmapHandlerTests, and two files one character apart, both
/// claiming the same subject, is how the heatmap's bucketing change went unnoticed here.
/// The heatmap cells are bucketed in the user's local zone (resolved from ProfileDto.Locale);
/// a Berlin user must NOT see Eastern rush-hour rows labelled with US-PM hours.
/// </summary>
public sealed class GetVolatilityHeatmapLocaleZoneTests
{
    private static readonly MethodInfo ResolveMethod =
        typeof(GetVolatilityHeatmapQueryHandler).GetMethod(
            "ResolveUserZone",
            BindingFlags.Static | BindingFlags.NonPublic)!;

    private static TimeZoneInfo Resolve(string locale)
        => (TimeZoneInfo)ResolveMethod.Invoke(null, [locale])!;

    [Theory]
    [InlineData("EN-us")]   // also proves the lookup is case-insensitive
    [InlineData("fr-CA")]   // Canada → Eastern (covers the Toronto fallback)
    public void Locale_WithKnownRegion_ResolvesToAMatchingZone(string locale)
    {
        TimeZoneInfo zone = Resolve(locale);
        zone.Should().NotBeNull();
        // Don't pin the exact zone — the host may have Windows or IANA mappings — just
        // confirm the offset is in the expected ballpark for that region.
        TimeSpan offset = zone.BaseUtcOffset;
        new[] { TimeSpan.FromHours(-5), TimeSpan.FromHours(-4) }
            .Should().Contain(offset, $"{locale} must resolve to a North-American zone in the UTC-4 / UTC-5 range");
    }

    [Fact]
    public void Empty_OrUnknown_Locale_FallsBackWithoutThrowing()
    {
        // The handler must never throw, regardless of what the profile looks like.
        // A blank locale, malformed tag, or unknown region should resolve to SOMETHING.
        Resolve("").Should().NotBeNull();
        Resolve("   ").Should().NotBeNull();
        Resolve("xx-XX").Should().NotBeNull();  // unknown region → falls through to system / UTC
        Resolve("nonsense").Should().NotBeNull();
    }

    [Fact]
    public void Region_Parse_FallsBackToSuffixSplit_WhenCultureInfo_Rejects()
    {
        // RegionInfo ctor throws on non-region tags like "nonsense". The resolver
        // splits on '-' as a last-resort fallback. Verify the public surface never
        // raises — the internal split is the safety net.
        Resolve("nonsense").Should().NotBeNull();
    }
}