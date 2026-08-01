using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PoTraffic.API.Features.Alerts;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.Alerts;
using WebPush;

namespace PoTraffic.API.Features.Alerts;

/// <summary>
/// Supplies the VAPID key pair for Web Push (#1). Reads <c>Push:VapidPublicKey</c>/
/// <c>Push:VapidPrivateKey</c>/<c>Push:Subject</c> from config (Key Vault in prod); when
/// unset it generates an ephemeral pair for the process so local dev works out of the box —
/// browser subscriptions then reset on restart, which is fine for development.
/// </summary>
public sealed class VapidKeyProvider
{
    public string PublicKey { get; }
    private readonly string _privateKey;
    private readonly string _subject;

    public VapidKeyProvider(IConfiguration config, ILogger<VapidKeyProvider> logger)
    {
        string? pub = config["Push:VapidPublicKey"];
        string? priv = config["Push:VapidPrivateKey"];
        _subject = config["Push:Subject"] is { Length: > 0 } s ? s : "mailto:admin@potraffic.dev";

        if (!string.IsNullOrWhiteSpace(pub) && !string.IsNullOrWhiteSpace(priv))
        {
            PublicKey = pub;
            _privateKey = priv;
        }
        else
        {
            VapidDetails generated = VapidHelper.GenerateVapidKeys();
            PublicKey = generated.PublicKey;
            _privateKey = generated.PrivateKey;
            logger.LogWarning(
                "Push: no VAPID keys configured — generated an ephemeral pair for this process. " +
                "Set Push:VapidPublicKey / Push:VapidPrivateKey (Key Vault) for stable subscriptions.");
        }
    }

    public VapidDetails Details => new(_subject, PublicKey, _privateKey);
}

public interface IPushNotifier
{
    Task SendAsync(UserId userId, AlertDto alert, CancellationToken ct = default);
}

/// <summary>Delivers an alert to every registered browser subscription for a user, pruning
/// subscriptions the push service reports as expired (404/410).</summary>
public sealed class WebPushNotifier(
    TableStorageContext db,
    VapidKeyProvider vapid,
    ILogger<WebPushNotifier> logger) : IPushNotifier
{
    private readonly WebPushClient _client = new();

    public async Task SendAsync(UserId userId, AlertDto alert, CancellationToken ct = default)
    {
        List<UserPushSubscription> subs = db.PushSubscriptions.Where(s => s.UserId == userId).ToList();
        if (subs.Count == 0)
            return;

        string payload = JsonSerializer.Serialize(new
        {
            title = alert.Kind == "Reroute" ? "Route changed" : "Heavier traffic",
            body = alert.Message,
            routeId = alert.RouteId,
            kind = alert.Kind,
        });

        VapidDetails details = vapid.Details;
        List<UserPushSubscription> expired = [];

        foreach (UserPushSubscription s in subs)
        {
            try
            {
                await _client.SendNotificationAsync(
                    new WebPush.PushSubscription(s.Endpoint, s.P256dh, s.Auth), payload, details);
            }
            catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
            {
                expired.Add(s);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Push send failed for user {UserId}", userId);
            }
        }

        if (expired.Count > 0)
        {
            foreach (UserPushSubscription e in expired)
                db.Remove(e);
            await db.SaveChangesAsync(ct);
        }
    }
}
