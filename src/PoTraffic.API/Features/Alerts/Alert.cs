namespace PoTraffic.API.Features.Alerts;

/// <summary>
/// A proactive alert raised when a route's live travel time crosses its baseline + σ (#1).
/// Persisted (partitioned by user) and surfaced in the in-app notification center; also
/// pushed to any registered browser push subscriptions.
/// </summary>
public sealed class Alert
{
    public AlertId Id { get; set; }
    public UserId UserId { get; set; }
    public RouteId RouteId { get; set; }
    public SessionId? SessionId { get; set; }

    /// <summary>"Congestion" (over baseline + σ) or "Reroute".</summary>
    public string Kind { get; set; } = "Congestion";
    public string Message { get; set; } = string.Empty;
    public int TravelSeconds { get; set; }
    public int BaselineSeconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsRead { get; set; }
}
