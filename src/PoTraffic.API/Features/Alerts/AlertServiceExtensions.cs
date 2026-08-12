namespace PoTraffic.API.Features.Alerts;

internal static class AlertServiceExtensions
{
    /// <summary>Registers the in-app proactive-alert pipeline. Web Push was removed — the
    /// NotificationBell in the client now only displays alerts the user reads in-app.</summary>
    internal static IServiceCollection AddAlertServices(this IServiceCollection services)
    {
        services.AddScoped<AlertEvaluator>();
        return services;
    }
}
