using System.Diagnostics;
using OpenTelemetry.Trace;

namespace PoTraffic.Api.Infrastructure.Observability;

/// <summary>
/// Composite sampler that routes background-job traces through a
/// <see cref="TraceIdRatioBased"/> sampler (50 %) to reduce noise, while all
/// other activity sources are always recorded.
/// </summary>
public sealed class CompositeRoutingSampler : Sampler
{
    // Strategy pattern — swaps sampling algorithm based on activity source origin
    private static readonly Sampler s_jobSampler = new TraceIdRatioBasedSampler(0.5);

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        Activity? parent = Activity.Current;

        if (parent is not null &&
            parent.Source.Name.StartsWith("PoTraffic.Scheduler", StringComparison.OrdinalIgnoreCase))
        {
            return s_jobSampler.ShouldSample(in samplingParameters);
        }

        return new SamplingResult(SamplingDecision.RecordAndSample);
    }
}
