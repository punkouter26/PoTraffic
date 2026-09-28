using FluentAssertions;
using PoTraffic.API.Features.History;
using Xunit;

namespace PoTraffic.UnitTests.Features.History;

/// <summary>The leave-by rule: the latest slot whose 90th-percentile trip still arrives on time.</summary>
public sealed class DeparturePlanTests
{
    private static int At(int h, int m) => h * 60 + m;

    [Fact]
    public void Build_PicksLatestSlotWhoseWorstTripArrivesInTime()
    {
        (int, int)[] samples =
        [
            // 07:40 slot: 30–40 min → worst arrives 08:20, on time for 08:30.
            (At(7, 40), 30 * 60), (At(7, 41), 35 * 60), (At(7, 42), 40 * 60),
            // 07:50 slot: usually 30 min but once 45 → worst arrives 08:35, late.
            (At(7, 50), 30 * 60), (At(7, 51), 30 * 60), (At(7, 52), 45 * 60),
        ];

        var plan = GetDeparturePlanQueryHandler.Build(samples, At(8, 30));

        plan.Should().NotBeNull();
        plan!.LeaveByMinute.Should().Be(At(7, 40), "07:50 is on time on average but not 9 days in 10");
        plan.P90Seconds.Should().Be(40 * 60);
        plan.OnTimePercent.Should().Be(100);
    }

    [Fact]
    public void Build_WithTooFewSamplesPerSlot_ReturnsNull()
    {
        (int, int)[] samples = [(At(7, 40), 30 * 60), (At(7, 41), 30 * 60)];

        GetDeparturePlanQueryHandler.Build(samples, At(8, 30)).Should().BeNull();
    }
}
