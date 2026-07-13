namespace PoTraffic.Api.Features.Alerts.Entities;

/// <summary>
/// A browser Web Push subscription for a user (#1). Stored per user; the endpoint + keys
/// are handed to the WebPush client to deliver encrypted push messages. Named to avoid a
/// clash with <c>WebPush.PushSubscription</c>.
/// </summary>
public sealed class UserPushSubscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
