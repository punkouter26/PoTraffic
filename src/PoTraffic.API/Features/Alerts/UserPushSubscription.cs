namespace PoTraffic.API.Features.Alerts;

/// <summary>
/// A browser Web Push subscription for a user. The endpoint + keys are what the push
/// service needs to deliver an encrypted message to that browser. Named to avoid a clash
/// with <c>Lib.Net.Http.WebPush.PushSubscription</c>.
/// </summary>
public sealed class UserPushSubscription
{
    public PushSubscriptionId Id { get; set; }
    public UserId UserId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
