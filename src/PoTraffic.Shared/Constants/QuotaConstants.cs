namespace PoTraffic.Shared.Constants;

public static class QuotaConstants
{
    public const int DefaultDailyQuota = 10;
    public const int RerouteDistanceThresholdPercent = 15;
    public const int BaselineMinSessionCount = 3;

    // Samples at which a route's baseline is treated as trustworthy enough to plan
    // against. Below it the client shows how far along the route is rather than
    // presenting an estimate built on a handful of points as settled fact.
    public const int BaselineConfidentSampleCount = 60;
    // Polls under one weather condition before its effect is presented as a number rather
    // than as "still watching". Twelve is an hour of fast-cadence polling — enough that one
    // freak crash on one rainy morning cannot define what rain costs.
    public const int WeatherImpactMinSamples = 12;

    public const int PollIntervalMinutes = 5;
    public const int RetentionDays = 90;

    // Adaptive polling (#8): when the last hour of samples is stable (coefficient of
    // variation ≤ threshold) the poll cadence backs off from PollIntervalMinutes to
    // StablePollIntervalMinutes to cut provider cost; volatile routes stay at the fast rate.
    public const int StablePollIntervalMinutes = 15;
    public const int AdaptiveSampleWindow = 12;
    public const int AdaptiveMinSamples = 4;
    public const double StableVolatilityThreshold = 0.08;
}
