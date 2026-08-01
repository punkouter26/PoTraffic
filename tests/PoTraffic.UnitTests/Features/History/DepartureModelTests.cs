using FluentAssertions;

using PoTraffic.Client.Features.Routes;
using PoTraffic.Shared.DTOs.History;

namespace PoTraffic.UnitTests.Features.History;

/// <summary>
/// Covers the client-side departure solver behind the "arrive by" planner. This is the
/// arithmetic that tells a user what time to leave, so its edges — sparse baselines,
/// unreachable arrivals, confidence bands — are worth pinning down.
/// </summary>
public sealed class DepartureModelTests
{
    /// <summary>Builds a slot at the given quarter-hour index.</summary>
    private static BaselineSlotDto Slot(int bucket, double meanSeconds, double? stdDevSeconds = 0, int samples = 10)
        => new("Monday", bucket, meanSeconds, stdDevSeconds, samples);

    /// <summary>Quarter-hour index for a wall-clock time.</summary>
    private static int SlotAt(int hour, int minute = 0) => (hour * 4) + (minute / 15);

    [Fact]
    public void Build_WithNoSlots_IsEmpty()
    {
        DepartureModel model = DepartureModel.Build([]);

        model.IsEmpty.Should().BeTrue();
        model.SolveLatestDeparture(9 * 60).Should().BeNull();
    }

    [Fact]
    public void Build_IgnoresSlotsWithNoSamples()
    {
        DepartureModel model = DepartureModel.Build([Slot(SlotAt(8), 1800, samples: 0)]);

        model.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void PredictSeconds_UsesObservedMeanForObservedSlot()
    {
        DepartureModel model = DepartureModel.Build([Slot(SlotAt(8), 1800)]);

        model.PredictSeconds(SlotAt(8)).Should().Be(1800);
        model.HasSamples(SlotAt(8)).Should().BeTrue();
    }

    [Fact]
    public void PredictSeconds_AddsStandardDeviationScaledByConfidence()
    {
        DepartureModel model = DepartureModel.Build([Slot(SlotAt(8), 1800, stdDevSeconds: 300)]);

        // z = 1 lands exactly one sigma above the mean.
        model.PredictSeconds(SlotAt(8), z: 1).Should().Be(2100);
        model.PredictSeconds(SlotAt(8), z: 0).Should().Be(1800);
    }

    [Fact]
    public void PredictSeconds_NeverReturnsNegative()
    {
        DepartureModel model = DepartureModel.Build([Slot(SlotAt(8), 600, stdDevSeconds: 500)]);

        // A large negative quantile must not produce a negative travel time.
        model.PredictSeconds(SlotAt(8), z: -5).Should().Be(0);
    }

    [Fact]
    public void Build_InterpolatesGapsBetweenObservedSlots()
    {
        // 08:00 = 20 min, 09:00 = 40 min. 08:30 sits halfway with no samples of its own.
        DepartureModel model = DepartureModel.Build([
            Slot(SlotAt(8), 1200),
            Slot(SlotAt(9), 2400)
        ]);

        model.PredictSeconds(SlotAt(8, 30)).Should().Be(1800);
        model.HasSamples(SlotAt(8, 30)).Should().BeFalse();
        model.ObservedSlots.Should().Be(2);
    }

    [Fact]
    public void Build_CarriesNearestObservationToTheEndsOfTheDay()
    {
        DepartureModel model = DepartureModel.Build([
            Slot(SlotAt(8), 1200),
            Slot(SlotAt(9), 2400)
        ]);

        // Before the first and after the last observation the curve flattens rather
        // than falling to zero, so scrubbing off the sampled range stays sensible.
        model.PredictSeconds(0).Should().Be(1200);
        model.PredictSeconds(DepartureModel.SlotsPerDay - 1).Should().Be(2400);
    }

    [Fact]
    public void SolveLatestDeparture_ReturnsLatestSlotThatStillArrivesInTime()
    {
        // A flat 30-minute commute all day.
        DepartureModel model = DepartureModel.Build([
            Slot(SlotAt(7), 1800),
            Slot(SlotAt(10), 1800)
        ]);

        DepartureSolution? solution = model.SolveLatestDeparture(9 * 60);

        // 08:30 + 30 min arrives exactly at 09:00; 08:45 would arrive at 09:15.
        solution.Should().NotBeNull();
        solution!.DepartureMinuteOfDay.Should().Be((8 * 60) + 30);
        solution.ArrivalMinuteOfDay.Should().Be(9 * 60);
        solution.SlackMinutes.Should().Be(0);
    }

    [Fact]
    public void SolveLatestDeparture_HigherConfidenceLeavesNoLaterThanLowerConfidence()
    {
        DepartureModel model = DepartureModel.Build([
            Slot(SlotAt(6), 1800, stdDevSeconds: 600),
            Slot(SlotAt(10), 1800, stdDevSeconds: 600)
        ]);

        DepartureSolution? typical = model.SolveLatestDeparture(9 * 60, ConfidenceLevel.Typical.Z);
        DepartureSolution? verySafe = model.SolveLatestDeparture(9 * 60, ConfidenceLevel.VerySafe.Z);

        typical.Should().NotBeNull();
        verySafe.Should().NotBeNull();
        verySafe!.DepartureMinuteOfDay.Should().BeLessThan(typical!.DepartureMinuteOfDay);
    }

    [Fact]
    public void SolveLatestDeparture_ReturnsNullWhenArrivalIsUnreachable()
    {
        // A two-hour commute cannot reach an 01:00 arrival from any same-day departure.
        DepartureModel model = DepartureModel.Build([Slot(SlotAt(0), 7200), Slot(SlotAt(23), 7200)]);

        model.SolveLatestDeparture(60).Should().BeNull();
    }

    [Fact]
    public void SolveLatestDeparture_FlagsAnInterpolatedDeparture()
    {
        // Observations at 06:00 and 10:00 only; the answer lands in the interpolated gap.
        DepartureModel model = DepartureModel.Build([
            Slot(SlotAt(6), 1800),
            Slot(SlotAt(10), 1800)
        ]);

        DepartureSolution? solution = model.SolveLatestDeparture(9 * 60);

        solution.Should().NotBeNull();
        solution!.IsInterpolated.Should().BeTrue();
    }

    [Fact]
    public void SolveLatestDeparture_PrefersTheFasterWindowWhenItExists()
    {
        // A congested 08:00–08:30 (65 min) either side of a clear 07:00 (20 min).
        // Leaving anywhere in the jam misses a 09:00 arrival, so the only workable
        // departures are before it.
        DepartureModel model = DepartureModel.Build([
            Slot(SlotAt(7), 1200),
            Slot(SlotAt(8), 3900),
            Slot(SlotAt(8, 30), 3900),
            Slot(SlotAt(9), 1200)
        ]);

        DepartureSolution? solution = model.SolveLatestDeparture(9 * 60);

        solution.Should().NotBeNull();
        solution!.ArrivalMinuteOfDay.Should().BeLessThanOrEqualTo(9 * 60);
        solution.DepartureMinuteOfDay.Should().BeLessThan(8 * 60);
    }

    [Fact]
    public void TotalSamples_SumsEveryObservedSlot()
    {
        DepartureModel model = DepartureModel.Build([
            Slot(SlotAt(8), 1800, samples: 12),
            Slot(SlotAt(9), 1900, samples: 30)
        ]);

        model.TotalSamples.Should().Be(42);
    }

    [Fact]
    public void Range_SpansTheFastestAndSlowestPredictedSlots()
    {
        DepartureModel model = DepartureModel.Build([
            Slot(SlotAt(8), 1200),
            Slot(SlotAt(9), 3600)
        ]);

        model.Range.MinSeconds.Should().Be(1200);
        model.Range.MaxSeconds.Should().Be(3600);
    }
}
