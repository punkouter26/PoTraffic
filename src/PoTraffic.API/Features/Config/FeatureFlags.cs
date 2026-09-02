namespace PoTraffic.API.Features.Config;

/// <summary>
/// Feature decisions resolved once at composition root and registered in DI.
///
/// <para>
/// These are derived from configuration <em>and</em> the host environment, and more than one
/// component acts on the same answer: provider registration swaps in the mocks, the health
/// checks short-circuit, and <c>/api/system/features</c> tells the client whether to show the
/// "USING MOCK DATA" banner. Recomputing the rule at each consumer let them disagree — the
/// endpoint read config only, so a Testing host without <c>Features:UseMockProviders</c> ran
/// on mocks while telling the client it was live.
/// </para>
/// </summary>
public sealed record FeatureFlags(bool UseMockProviders, bool EnableWeather)
{
    /// <summary>Evaluates the flags from configuration and the host environment.</summary>
    public static FeatureFlags Resolve(IConfiguration configuration, IHostEnvironment environment) => new(
        UseMockProviders: environment.IsEnvironment("Testing")
            || configuration.GetValue<bool>("Features:UseMockProviders"),
        // Defaults on: the conditions feed needs no key and no secret, and a route with
        // weather recorded from day one can answer "is rain the reason" months later —
        // whereas switching it on late leaves a permanent blind spot in the history.
        EnableWeather: configuration.GetValue("Features:EnableWeather", true));
}
