using System.Diagnostics;
using OpenTelemetry.Trace;

namespace PoTraffic.API.Infrastructure.Observability;

/// <summary>
/// Head sampler implementing the §6.3 telemetry budget:
/// <list type="bullet">
///   <item>Dev / Test → 100 % capture (every trace recorded).</item>
///   <item>Prod → rate-limited: at most <c>tracesPerSecond</c> healthy request traces and
///   <c>jobsPerSecond</c> background-job traces are sampled each rolling second; the rest are
///   dropped to stay under the App Insights ingest quota.</item>
///   <item>Error-bearing and parent-sampled spans bypass the rate limiter (best-effort 100 %
///   error retention that a head sampler can observe — a fully error-complete trace requires
///   a tail/collector stage, which is out of scope here).</item>
/// </list>
/// </summary>
public sealed class CompositeRoutingSampler : Sampler
{
    private static readonly SamplingResult Record = new(SamplingDecision.RecordAndSample);
    private static readonly SamplingResult Drop = new(SamplingDecision.Drop);

    private readonly bool _isProduction;
    private readonly RateLimiter _healthy;
    private readonly RateLimiter _jobs;

    public CompositeRoutingSampler(bool isProduction, int tracesPerSecond = 10, int jobsPerSecond = 1)
    {
        _isProduction = isProduction;
        _healthy = new RateLimiter(tracesPerSecond);
        _jobs = new RateLimiter(jobsPerSecond);
    }

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        // Dev / Test — capture everything.
        if (!_isProduction)
            return Record;

        // Honour an upstream decision: if the parent context was sampled, keep the child so
        // traces are not truncated mid-flight.
        if ((samplingParameters.ParentContext.TraceFlags & ActivityTraceFlags.Recorded) != 0)
            return Record;

        // Errors that are already observable at span start bypass the rate limiter.
        if (HasErrorSignal(samplingParameters))
            return Record;

        RateLimiter limiter = IsBackgroundJob() ? _jobs : _healthy;
        return limiter.TryAcquire() ? Record : Drop;
    }

    // Background-job activities originate under the scheduler ActivitySource.
    private static bool IsBackgroundJob()
        => Activity.Current?.Source.Name.StartsWith("PoTraffic.Scheduler", StringComparison.OrdinalIgnoreCase) == true;

    private static bool HasErrorSignal(in SamplingParameters p)
    {
        if (p.Tags is null)
            return false;
        foreach (KeyValuePair<string, object?> tag in p.Tags)
        {
            if ((tag.Key is "error" && tag.Value is true or "true")
                || (tag.Key is "otel.status_code" && tag.Value is "ERROR")
                || tag.Key is "exception.type")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Token bucket: allows up to <c>perSecond</c> acquisitions per rolling second.</summary>
    private sealed class RateLimiter(int perSecond)
    {
        private readonly Lock _gate = new();
        private long _windowStart = Stopwatch.GetTimestamp();
        private int _count;

        public bool TryAcquire()
        {
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                if (now - _windowStart >= Stopwatch.Frequency)
                {
                    _windowStart = now;
                    _count = 0;
                }
                if (_count >= perSecond)
                    return false;
                _count++;
                return true;
            }
        }
    }
}
