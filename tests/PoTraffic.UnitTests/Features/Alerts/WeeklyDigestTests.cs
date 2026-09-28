using FluentAssertions;
using PoTraffic.API.Features.Alerts;
using Xunit;

namespace PoTraffic.UnitTests.Features.Alerts;

public sealed class WeeklyDigestTests
{
    // Monday 28 Sep 2026, noon. Last week = Mon 21 – Sun 27.
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static (DateTimeOffset, int) Trip(int day, int h, int m, int minutes) =>
        (new DateTimeOffset(2026, 9, day, h, m, 0, TimeSpan.Zero), minutes * 60);

    [Fact]
    public void Summarize_NamesBestAndWorstDay_ComparesWithEarlierWeeks_AndValuesTheDepartureTime()
    {
        List<(DateTimeOffset, int)> samples =
        [
            // Earlier weeks: 30 min at 07:30, 40 min at 08:00.
            Trip(8, 7, 30, 30), Trip(9, 7, 30, 30), Trip(10, 7, 30, 30),
            Trip(8, 8, 0, 40), Trip(9, 8, 0, 40), Trip(10, 8, 0, 40),
            // Last week: Tuesday quick, Thursday slow.
            Trip(22, 7, 30, 30), Trip(22, 7, 31, 32),
            Trip(24, 8, 0, 44), Trip(24, 8, 1, 46),
        ];

        string? text = WeeklyDigestJob.Summarize(samples, Now);

        text.Should().Contain("Tuesday was quickest (31 min), Thursday slowest (45 min).");
        text.Should().Contain("About 3 min slower than the weeks before.");
        text.Should().Contain("Leaving at 07:30 instead of 08:00 saves about 12 min a trip.");
    }

    [Fact]
    public void Summarize_WithAQuietWeek_SaysNothing() =>
        WeeklyDigestJob.Summarize([Trip(22, 7, 30, 30)], Now).Should().BeNull();
}
