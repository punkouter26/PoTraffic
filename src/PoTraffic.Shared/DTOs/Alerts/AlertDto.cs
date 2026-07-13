namespace PoTraffic.Shared.DTOs.Alerts;

public sealed record AlertDto(
    Guid Id,
    Guid RouteId,
    string Kind,
    string Message,
    int TravelSeconds,
    int BaselineSeconds,
    DateTimeOffset CreatedAt,
    bool IsRead);

/// <summary>Browser push subscription payload sent from the client after it subscribes.</summary>
public sealed record PushSubscriptionRequest(
    string Endpoint,
    string P256dh,
    string Auth);

public sealed record VapidPublicKeyResponse(string PublicKey);
