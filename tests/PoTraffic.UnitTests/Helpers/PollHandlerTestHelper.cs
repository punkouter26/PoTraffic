using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PoTraffic.API.Features.Config;
using PoTraffic.API.Features.Routes;
using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.API.Infrastructure.Storage;

namespace PoTraffic.UnitTests.Helpers;

/// <summary>
/// Builds <see cref="ExecutePollCommandHandler"/> for the unit tests that drive it directly.
///
/// <para>
/// Constructing it inline in each test meant every new handler dependency broke ten call
/// sites that had no interest in it. The signature lives here now, and a test states only
/// what it actually cares about.
/// </para>
/// </summary>
internal static class PollHandlerTestHelper
{
    /// <param name="weather">
    /// Supply one to exercise weather capture. The default leaves <c>EnableWeather</c> off,
    /// so tests about polling and reroute detection are not also tests about the conditions
    /// feed.
    /// </param>
    public static ExecutePollCommandHandler Create(
        TableStorageContext db,
        ITrafficProviderFactory providerFactory,
        ILogger<ExecutePollCommandHandler>? logger = null,
        IWeatherProvider? weather = null) =>
        new(db,
            providerFactory,
            weather ?? new NoWeatherProvider(),
            new FeatureFlags(UseMockProviders: true, EnableWeather: weather is not null),
            AlertTestHelper.NoOp(db),
            logger ?? NullLogger<ExecutePollCommandHandler>.Instance);

    /// <summary>Answers "no observation" — the same shape as a real provider outage.</summary>
    private sealed class NoWeatherProvider : IWeatherProvider
    {
        public Task<WeatherObservation?> GetCurrentAsync(string coordinates, CancellationToken ct = default) =>
            Task.FromResult<WeatherObservation?>(null);
    }
}
