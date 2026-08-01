using PoTraffic.Shared.DTOs.History;

namespace PoTraffic.Client.Features.Routes;

/// <summary>
/// A day's travel-time curve, indexed by quarter-hour slot, built entirely from the
/// baseline the page has already downloaded.
///
/// <para>
/// This is what makes the departure scrubber and the "arrive by" solver instant: every
/// answer is an array lookup, so dragging the slider costs nothing and never touches
/// the network. The server's <c>/optimal-departure</c> endpoint answers one fixed
/// question ("when is this route quickest?"); this answers the one users actually ask
/// — "I need to be there at 09:00, when do I leave?"
/// </para>
///
/// <para>
/// Baseline slots arrive sparse: only quarter-hours that have samples are present. Gaps
/// between two observed slots are linearly interpolated, and the ends carry the nearest
/// observation outward, so scrubbing across an unsampled stretch degrades smoothly
/// instead of dropping to zero. <see cref="HasSamples"/> reports which slots are real,
/// so the UI can be honest about where it is guessing.
/// </para>
/// </summary>
public sealed class DepartureModel
{
    /// <summary>Quarter-hour slots in a day — the bucket scheme <c>GetBaselineQuery</c> emits.</summary>
    public const int SlotsPerDay = 96;

    /// <summary>Minutes covered by one slot.</summary>
    public const int SlotMinutes = 24 * 60 / SlotsPerDay;

    private readonly double[] _mean = new double[SlotsPerDay];
    private readonly double[] _stdDev = new double[SlotsPerDay];
    private readonly int[] _samples = new int[SlotsPerDay];
    private readonly bool[] _observed = new bool[SlotsPerDay];

    private DepartureModel() { }

    /// <summary>True when no slot had any samples — the caller should show the empty state.</summary>
    public bool IsEmpty { get; private set; } = true;

    /// <summary>Total samples behind the curve, across every slot.</summary>
    public int TotalSamples { get; private set; }

    /// <summary>Number of quarter-hours that were actually observed rather than interpolated.</summary>
    public int ObservedSlots { get; private set; }

    /// <summary>Fastest and slowest predicted durations across the day, in seconds. Used to scale the strip.</summary>
    public (double MinSeconds, double MaxSeconds) Range { get; private set; }

    /// <summary>True when <paramref name="slot"/> came from real samples rather than interpolation.</summary>
    public bool HasSamples(int slot) => _observed[Wrap(slot)];

    /// <summary>Sample count behind <paramref name="slot"/>; 0 for interpolated slots.</summary>
    public int SampleCount(int slot) => _samples[Wrap(slot)];

    /// <summary>
    /// Predicted travel time in seconds for a departure in <paramref name="slot"/>, at the
    /// given confidence. <paramref name="z"/> is the standard-normal quantile: 0 is the
    /// median outcome, 1.28 the "late one commute in ten" outcome.
    /// </summary>
    public double PredictSeconds(int slot, double z = 0)
    {
        int i = Wrap(slot);
        return Math.Max(0, _mean[i] + (z * _stdDev[i]));
    }

    /// <summary>Standard deviation in seconds for <paramref name="slot"/> — the volatility itself.</summary>
    public double StdDevSeconds(int slot) => _stdDev[Wrap(slot)];

    /// <summary>
    /// The latest departure slot that still arrives by <paramref name="arrivalMinuteOfDay"/>
    /// at the requested confidence, or null when even the earliest modelled departure
    /// cannot make it.
    /// </summary>
    /// <remarks>
    /// Walks backwards from the arrival time rather than forwards from midnight: the answer
    /// wanted is the <em>last</em> safe moment to leave, and the first match walking back is
    /// exactly that. Only same-day departures are considered — a commute that would have to
    /// start before midnight is reported as unreachable rather than silently wrapping.
    /// </remarks>
    public DepartureSolution? SolveLatestDeparture(int arrivalMinuteOfDay, double z = 0)
    {
        if (IsEmpty)
            return null;

        int latestSlot = Math.Min(SlotsPerDay - 1, arrivalMinuteOfDay / SlotMinutes);

        for (int slot = latestSlot; slot >= 0; slot--)
        {
            double durationSeconds = PredictSeconds(slot, z);
            double arrivalMinute = (slot * SlotMinutes) + (durationSeconds / 60.0);

            if (arrivalMinute <= arrivalMinuteOfDay)
            {
                return new DepartureSolution(
                    DepartureSlot: slot,
                    DepartureMinuteOfDay: slot * SlotMinutes,
                    PredictedSeconds: durationSeconds,
                    ArrivalMinuteOfDay: arrivalMinute,
                    SlackMinutes: arrivalMinuteOfDay - arrivalMinute,
                    IsInterpolated: !_observed[slot]);
            }
        }

        return null;
    }

    /// <summary>Builds the curve from a baseline response's slots.</summary>
    public static DepartureModel Build(IReadOnlyList<BaselineSlotDto> slots)
    {
        DepartureModel model = new();

        foreach (BaselineSlotDto slot in slots)
        {
            int i = slot.TimeSlotBucket;
            if (i < 0 || i >= SlotsPerDay || slot.SessionCount <= 0)
                continue;

            model._mean[i] = slot.MeanDurationSeconds;
            model._stdDev[i] = slot.StdDevDurationSeconds ?? 0;
            model._samples[i] = slot.SessionCount;
            model._observed[i] = true;
        }

        model.ObservedSlots = model._observed.Count(o => o);
        if (model.ObservedSlots == 0)
            return model; // IsEmpty stays true; every accessor returns zeroes.

        model.FillGaps();

        model.IsEmpty = false;
        model.TotalSamples = model._samples.Sum();
        model.Range = (model._mean.Min(), model._mean.Max());
        return model;
    }

    /// <summary>
    /// Linearly interpolates unobserved slots between their nearest observed neighbours, and
    /// carries the first/last observation out to the ends of the day.
    /// </summary>
    private void FillGaps()
    {
        int first = Array.FindIndex(_observed, o => o);
        int last = Array.FindLastIndex(_observed, o => o);

        for (int i = 0; i < first; i++)
        {
            _mean[i] = _mean[first];
            _stdDev[i] = _stdDev[first];
        }

        for (int i = last + 1; i < SlotsPerDay; i++)
        {
            _mean[i] = _mean[last];
            _stdDev[i] = _stdDev[last];
        }

        int prev = first;
        for (int i = first + 1; i <= last; i++)
        {
            if (!_observed[i])
                continue;

            int gap = i - prev;
            for (int step = 1; step < gap; step++)
            {
                double t = (double)step / gap;
                _mean[prev + step] = _mean[prev] + ((_mean[i] - _mean[prev]) * t);
                _stdDev[prev + step] = _stdDev[prev] + ((_stdDev[i] - _stdDev[prev]) * t);
            }
            prev = i;
        }
    }

    private static int Wrap(int slot) => Math.Clamp(slot, 0, SlotsPerDay - 1);
}

/// <summary>An answer to "when do I leave to arrive by X?".</summary>
/// <param name="DepartureSlot">Quarter-hour index of the departure.</param>
/// <param name="DepartureMinuteOfDay">Departure time as minutes past midnight.</param>
/// <param name="PredictedSeconds">Predicted travel time at the requested confidence.</param>
/// <param name="ArrivalMinuteOfDay">Predicted arrival, minutes past midnight.</param>
/// <param name="SlackMinutes">Minutes to spare against the requested arrival.</param>
/// <param name="IsInterpolated">True when the departure slot had no samples of its own.</param>
public sealed record DepartureSolution(
    int DepartureSlot,
    int DepartureMinuteOfDay,
    double PredictedSeconds,
    double ArrivalMinuteOfDay,
    double SlackMinutes,
    bool IsInterpolated);

/// <summary>
/// How much of the volatility band to plan against, in user-facing terms. The z values are
/// standard-normal quantiles applied to each slot's own standard deviation.
/// </summary>
/// <param name="Label">Short control label.</param>
/// <param name="Description">What the choice means in plain language.</param>
/// <param name="Z">Standard-normal quantile applied to the slot's σ.</param>
public sealed record ConfidenceLevel(string Label, string Description, double Z)
{
    public static readonly ConfidenceLevel Typical =
        new("Typical", "on time about half the time", 0);

    public static readonly ConfidenceLevel Safe =
        new("Safe", "on time about 8 days in 10", 0.84);

    public static readonly ConfidenceLevel VerySafe =
        new("Very safe", "on time about 9 days in 10", 1.28);

    public static readonly IReadOnlyList<ConfidenceLevel> All = [Typical, Safe, VerySafe];
}
