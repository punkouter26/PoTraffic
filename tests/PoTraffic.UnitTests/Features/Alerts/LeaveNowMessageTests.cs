using FluentAssertions;
using PoTraffic.API.Features.Alerts;
using Xunit;

namespace PoTraffic.UnitTests.Features.Alerts;

/// <summary>When the arrive-by nudge fires: within 15 min of target − live trip − 5 min margin.</summary>
public sealed class LeaveNowMessageTests
{
    private static readonly TimeOnly ArriveBy = new(8, 30);
    private static DateTime Today(int h, int m) => new(2026, 9, 28, h, m, 0);

    [Fact]
    public void TooEarly_NoNudge() =>
        // Leave-at is 08:30 − 30 − 5 = 07:55; at 07:30 that's 25 min away.
        AlertEvaluator.LeaveNowMessage(Today(7, 30), ArriveBy, TimeSpan.FromMinutes(30)).Should().BeNull();

    [Fact]
    public void WithinLead_SaysHowLongUntilLeaving() =>
        AlertEvaluator.LeaveNowMessage(Today(7, 45), ArriveBy, TimeSpan.FromMinutes(30))
            .Should().StartWith("Leave in 10 min to arrive by 08:30");

    [Fact]
    public void PastLeaveTime_SaysHowLate() =>
        AlertEvaluator.LeaveNowMessage(Today(8, 10), ArriveBy, TimeSpan.FromMinutes(30))
            .Should().StartWith("Leave now — at 30 min you'd arrive about 10 min after 08:30");

    [Fact]
    public void AfterTarget_NoNudge() =>
        AlertEvaluator.LeaveNowMessage(Today(8, 31), ArriveBy, TimeSpan.FromMinutes(30)).Should().BeNull();
}
