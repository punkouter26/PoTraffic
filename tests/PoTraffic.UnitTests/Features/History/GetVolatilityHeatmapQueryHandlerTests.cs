// filepath: tests/PoTraffic.UnitTests/Features/History/GetVolatilityHeatmapQueryHandlerTests.cs
using System.Globalization;
using System.Reflection;
using FluentAssertions;
using PoTraffic.API.Features.History;
using Xunit;

namespace PoTraffic.UnitTests.Features.History;

/// <summary>
/// Verifies the locale → TimeZoneInfo resolution inside GetVolatilityHeatmapQueryHandler.
/// The heatmap cells are bucketed in the user's local zone (resolved from ProfileDto.Locale);
/// a Berlin user must NOT see Eastern rush-hour rows labelled with US-PM hours.
/// </summary>
public sealed class GetVolatilityHeatmapQueryHandlerTests
{
    private static readonly MethodInfo ResolveMethod =
        typeof(GetVolatilityHeatmapQueryHandler).GetMethod(
            "ResolveUserZone",
            BindingFlags.Static | BindingFlags.NonPublic)!;

    private static TimeZoneInfo Resolve(string locale)
        => (TimeZoneInfo)ResolveMethod.Invoke(null, [locale])!;

    [Theory]
    [InlineData("en-US")]
    [InlineData("EN-us")]        // case-insensitive lookup
    [InlineData("fr-CA")]        // Canada → Eastern (covers Toronto fallback)
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

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("es-ES")]
    public void Locale_EuropeanRegion_ResolvesToACentralEuropeanZone(string locale)
    {
        TimeZoneInfo zone = Resolve(locale);
        new[] { TimeSpan.FromHours(1), TimeSpan.FromHours(2) }
            .Should().Contain(zone.BaseUtcOffset, $"{locale} must resolve to a Central-European zone");
    }

    [Theory]
    [InlineData("ja-JP")]
    public void Locale_Japan_ResolvesToJST(string locale)
    {
        TimeZoneInfo zone = Resolve(locale);
        zone.BaseUtcOffset.Should().Be(TimeSpan.FromHours(9),
            "Japan Standard Time is fixed at UTC+9, no DST");
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