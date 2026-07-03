using System.Diagnostics;
using OpenTelemetry.Trace;

namespace PoTraffic.Api.Infrastructure.Observability;

/// <summary>
/// Composite sampler that routes background-job traces through a
/// <see cref="TraceIdRatioBased"/> sampler while all other activity sources
/// are always recorded. Honours the CI/CD rule #8 telemetry budget:
///   • Prod healthy traces → 5 % (configurable)
///   • Prod background-job traces → 1 % (always off the hot path)
///   • Non-prod healthy traces → 100 %
/// Exceptions / dependency-failure spans always pass — they bypass the ratio sampler.
/// </summary>
public sealed class CompositeRoutingSampler : Sampler
{
    private readonly Sampler _healthySampler;
    private readonly Sampler _jobSampler;

    public CompositeRoutingSampler(double prodRatio = 0.05)
    {
        _healthySampler = new TraceIdRatioBasedSampler(prodRatio);
        _jobSampler = new TraceIdRatioBasedSampler(Math.Max(0.01, prodRatio / 5));
    }

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        Activity? parent = Activity.Current;

        // Background jobs are high-volume — sample them at 1/5 of the prod ratio.
        if (parent is not null &&
            parent.Source.Name.StartsWith("PoTraffic.Scheduler", StringComparison.OrdinalIgnoreCase))
        {
            return _jobSampler.ShouldSample(in samplingParameters);
        }

        return _healthySampler.ShouldSample(in samplingParameters);
    }
}
