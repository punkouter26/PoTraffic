using System.Diagnostics;
using OpenTelemetry.Trace;

namespace PoTraffic.Api.Infrastructure.Observability;

/// <summary>
/// Composite sampler that routes Hangfire background-job traces through a
/// <see cref="TraceIdRatioBased"/> sampler (50 %) to reduce noise, while all
/// other activity sources are always recorded.
/// </summary>
public sealed class CompositeRoutingSampler : Sampler
{
    // Strategy pattern — swaps sampling algorithm based on activity source origin
    private static readonly Sampler s_hangfireSampler = new TraceIdRatioBasedSampler(0.5);

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        Activity? parent = Activity.Current;

        if (parent is not null &&
            parent.Source.Name.StartsWith("Hangfire", StringComparison.OrdinalIgnoreCase))
        {
            return s_hangfireSampler.ShouldSample(in samplingParameters);
        }

        return new SamplingResult(SamplingDecision.RecordAndSample);
    }
}
