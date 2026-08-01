// filepath: src/PoTraffic.API/Infrastructure/Resilience/ResiliencePipelineExtensions.cs
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace PoTraffic.API.Infrastructure.Resilience;

/// <summary>
/// Centralized .NET 10 <c>Microsoft.Extensions.Http.Resilience</c> configuration
/// for every external HTTP client in <c>PoTraffic.API</c>.
///
/// <para>
/// Replaces the .NET 9-compat blanket <c>AddStandardResilienceHandler()</c> call
/// (which applied a single shared pipeline to every named client) with the
/// modern <c>AddResilienceHandler(name, configure)</c> API so each external
/// dependency gets a tailored retry/breaker/timeout profile. Names are public
/// constants so any feature slice can reference the same pipeline.
/// </para>
///
/// <list type="bullet">
///   <item><c>traffic</c> — Google Maps / TomTom traffic APIs.
///     Aggressive timeout (8s), 2 retries with jitter, breaker at 50%.</item>
///   <item><c>external-auth</c> — Microsoft OAuth token + userinfo endpoints.
///     Slightly longer timeout (10s), 1 retry, breaker at 30%.</item>
/// </list>
/// </summary>
public static class ResiliencePipelineExtensions
{
    public const string TrafficPipeline = "traffic";
    public const string ExternalAuthPipeline = "external-auth";

    /// <summary>
    /// Configures every external HTTP client with its dependency-specific
    /// resilience pipeline. Each <c>AddHttpClient&lt;T&gt;()</c> in the codebase
    /// should chain <c>AddResilienceHandler(<see cref="TrafficPipeline"/>)</c> or
    /// <c>AddResilienceHandler(<see cref="ExternalAuthPipeline"/>)</c>.
    /// Returns the original <paramref name="builder"/> so additional
    /// configuration calls may continue to be chained.
    /// </summary>
    public static IHttpClientBuilder AddResilienceHandler(
        this IHttpClientBuilder builder,
        string pipelineName)
    {
        switch (pipelineName)
        {
            case TrafficPipeline:
                builder.AddResilienceHandler(TrafficPipeline, ConfigureTrafficPipeline);
                break;
            case ExternalAuthPipeline:
                builder.AddResilienceHandler(ExternalAuthPipeline, ConfigureExternalAuthPipeline);
                break;
            default:
                throw new ArgumentException(
                    $"Unknown PoTraffic resilience pipeline '{pipelineName}'. " +
                    $"Use '{TrafficPipeline}' or '{ExternalAuthPipeline}' (or add a new entry in {nameof(ResiliencePipelineExtensions)}).",
                    nameof(pipelineName));
        }
        return builder;
    }

    private static void ConfigureTrafficPipeline(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        builder
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(250),
                UseJitter = true,
            })
            .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(15),
            })
            .AddTimeout(TimeSpan.FromSeconds(8));
    }

    private static void ConfigureExternalAuthPipeline(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        builder
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.FromMilliseconds(500),
            })
            .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.3,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(60),
                BreakDuration = TimeSpan.FromSeconds(30),
            })
            .AddTimeout(TimeSpan.FromSeconds(10));
    }
}
