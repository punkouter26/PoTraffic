namespace PoTraffic.API.Features.Alerts;

internal static class AlertServiceExtensions
{
    /// <summary>Registers proactive-alert + Web Push services (#1).</summary>
    internal static IServiceCollection AddAlertServices(this IServiceCollection services)
    {
        services.AddSingleton<VapidKeyProvider>();
        services.AddScoped<IPushNotifier, WebPushNotifier>();
        services.AddScoped<AlertEvaluator>();
        return services;
    }
}
