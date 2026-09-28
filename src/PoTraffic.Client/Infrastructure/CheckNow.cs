using System.Net.Http.Json;
using PoTraffic.Client.Infrastructure.Http;
using Radzen;

namespace PoTraffic.Client.Infrastructure;

/// <summary>
/// The one user-initiated probe. The dashboard card and the route page used to carry a
/// copy each, with different labels ("Check now" / "Probe now") and different toasts
/// for the same POST — the one action that spends money should not have two
/// implementations that can drift.
/// </summary>
public static class CheckNow
{
    /// <summary>Runs a live check and reports the result as a toast. True on success.</summary>
    public static async Task<bool> RunAsync(HttpClient http, NotificationService notifications, Guid routeId)
    {
        try
        {
            HttpResponseMessage resp = await http.PostAsync($"/api/routes/{routeId}/check-now", null);
            if (resp.IsSuccessStatusCode)
            {
                CheckNowResponse? data = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.CheckNowResponse);
                notifications.Notify(new NotificationMessage
                {
                    Severity = NotificationSeverity.Info,
                    Summary = "Right now",
                    Detail = data is not null
                        ? $"{data.DurationSeconds / 60} min · {TextFormatting.Distance(data.DistanceMetres)}"
                        : "Live travel time retrieved",
                    Duration = 6000
                });
                return true;
            }

            Fail(notifications, resp.StatusCode switch
            {
                System.Net.HttpStatusCode.ServiceUnavailable => "Traffic provider unavailable — try again in a moment.",
                System.Net.HttpStatusCode.TooManyRequests => "Too many checks — wait a minute and try again.",
                _ => $"Request failed ({(int)resp.StatusCode})."
            });
        }
        // Timeouts surface as TaskCanceledException and a bad body as JsonException; left
        // uncaught, either one raised Blazor's unhandled-error bar from a button click.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Fail(notifications, "Unable to connect. Please try again.");
        }
        return false;
    }

    private static void Fail(NotificationService notifications, string detail) =>
        notifications.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Error,
            Summary = "Check failed",
            Detail = detail,
            Duration = 5000
        });
}
