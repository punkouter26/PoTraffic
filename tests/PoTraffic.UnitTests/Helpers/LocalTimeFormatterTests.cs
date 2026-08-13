// filepath: tests/PoTraffic.UnitTests/Helpers/LocalTimeFormatterTests.cs
using System.Globalization;
using FluentAssertions;
using PoTraffic.Client.Infrastructure;
using Xunit;

namespace PoTraffic.UnitTests.Helpers;

/// <summary>
/// The server stores monitoring-window times in UTC and ships them as plain
/// "HH:mm" strings on the wire. The client has to round them back to the user's
/// local wall-clock for display, otherwise an Eastern user picking 9:40 AM would
/// see "13:40" after save.
/// </summary>
public sealed class LocalTimeFormatterTests
{
    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private static readonly DateTime FixedNowUtc =
        new(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RoundTrips_EasternUser_9amLocal_Picks13Utc_Displays9amLocal()
    {
        // Eastern at 09:00 local in August = 13:00 UTC (EDT, UTC-4).
        LocalTimeFormatter.FormatUtcHHmmAsLocal("13:00", FixedNowUtc, Eastern)
            .Should().Be("9:00 AM");
    }

    [Fact]
    public void RoundTrips_EasternUser_9pmLocal_Picks1amUtcNextDay_Displays9pmLocal()
    {
        // The exact case the user just reproduced: 9:40 PM Eastern = 01:40 UTC next day.
        LocalTimeFormatter.FormatUtcHHmmAsLocal("01:40", FixedNowUtc, Eastern)
            .Should().Be("9:40 PM");
    }

    [Fact]
    public void PassesThrough_EmptyOrUnparseable()
    {
        LocalTimeFormatter.FormatUtcHHmmAsLocal("",    FixedNowUtc, Eastern).Should().Be("");
        LocalTimeFormatter.FormatUtcHHmmAsLocal("   ", FixedNowUtc, Eastern).Should().Be("   ");
        LocalTimeFormatter.FormatUtcHHmmAsLocal("not a time", FixedNowUtc, Eastern).Should().Be("not a time");
    }

    [Fact]
    public void WrapAround_StartAndEnd_ConvertIndependently()
    {
        // A window stored as 13:21 → 01:21 UTC must display as 9:21 AM → 9:21 PM
        // (Eastern), not as "1:21 AM → 1:21 AM" which would imply zero duration.
        string start = LocalTimeFormatter.FormatUtcHHmmAsLocal("13:21", FixedNowUtc, Eastern);
        string end   = LocalTimeFormatter.FormatUtcHHmmAsLocal("01:21", FixedNowUtc, Eastern);

        start.Should().Be("9:21 AM");
        end.Should().Be("9:21 PM");
    }

    [Fact]
    public void Format_RespectsCurrentCulture_AM_PM_Suffix()
    {
        // en-US uses "tt" for AM/PM. Pin the suffix for this culture only —
        // other cultures format the same input differently.
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            // 13:00 UTC on Aug 13 (DST) = 09:00 AM EDT, not 1:00 PM.
            string result = LocalTimeFormatter.FormatUtcHHmmAsLocal("13:00", FixedNowUtc, Eastern);

            result.Should().Be("9:00 AM");
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void Format_NeverIncludesUtcSuffix()
    {
        // Belt-and-suspenders: every output is a local-time string and must never
        // carry a "UTC" tag regardless of the culture producing the AM/PM designator.
        var prev = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "en-GB", "de-DE", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                string r = LocalTimeFormatter.FormatUtcHHmmAsLocal("13:00", FixedNowUtc, Eastern);
                r.Should().NotContain("UTC", $"culture {name} must format as local time");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }
}