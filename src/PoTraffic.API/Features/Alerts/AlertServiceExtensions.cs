namespace PoTraffic.API.Features.Alerts;

internal static class AlertServiceExtensions
{
    /// <summary>Registers proactive alerts: the in-app notification centre plus Web Push to
    /// every browser the user subscribed.</summary>
    internal static IServiceCollection AddAlertServices(this IServiceCollection services)
    {
        services.AddScoped<AlertEvaluator>();
        services.AddScoped<WeeklyDigestJob>();
        services.AddSingleton<VapidKeys>();
        services.AddHttpClient<IPushNotifier, WebPushNotifier>();
        return services;
    }
}
