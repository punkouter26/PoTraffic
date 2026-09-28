using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging;
using PoTraffic.API.Infrastructure.Storage;

namespace PoTraffic.API.Features.Alerts;

/// <summary>What the service worker shows: <c>push</c> handler in service-worker.js.</summary>
/// <param name="Tag">Notifications with the same tag replace each other instead of stacking.</param>
public sealed record PushPayload(string Title, string Body, string Url, string Tag);

public interface IPushNotifier
{
    /// <summary>Delivers to every browser the user subscribed. Never throws for delivery failures.</summary>
    Task SendAsync(UserId userId, PushPayload payload, TimeSpan timeToLive, CancellationToken ct = default);
}

/// <summary>
/// The VAPID key pair identifying this server to push services. Reads
/// <c>Push:VapidPublicKey</c> / <c>Push:VapidPrivateKey</c> / <c>Push:Subject</c> (Key Vault in
/// production). When unset it generates a pair for this process so local dev works out of the
/// box — browser subscriptions then stop working at the next restart, which is why it warns.
/// </summary>
public sealed class VapidKeys
{
    public string PublicKey { get; }
    public VapidAuthentication Authentication { get; }

    public VapidKeys(IConfiguration config, ILogger<VapidKeys> logger)
    {
        string? publicKey = config["Push:VapidPublicKey"];
        string? privateKey = config["Push:VapidPrivateKey"];
        if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(privateKey))
        {
            (publicKey, privateKey) = Generate();
            logger.LogWarning(
                "Push: no VAPID keys configured — generated a pair for this process. Set " +
                "Push:VapidPublicKey / Push:VapidPrivateKey for subscriptions that survive a restart.");
        }

        PublicKey = publicKey;
        Authentication = new VapidAuthentication(publicKey, privateKey)
        {
            Subject = config["Push:Subject"] is { Length: > 0 } s ? s : "mailto:admin@potraffic.dev",
        };
    }

    /// <summary>A P-256 pair in the URL-safe base64 form browsers and push services expect.</summary>
    private static (string PublicKey, string PrivateKey) Generate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters p = key.ExportParameters(includePrivateParameters: true);
        byte[] uncompressedPoint = [0x04, .. p.Q.X!, .. p.Q.Y!];
        return (Base64Url.EncodeToString(uncompressedPoint), Base64Url.EncodeToString(p.D!));
    }
}

/// <summary>Sends Web Push messages, pruning subscriptions the push service reports as gone (404/410).</summary>
public sealed class WebPushNotifier(
    HttpClient http,
    TableStorageContext db,
    VapidKeys vapid,
    ILogger<WebPushNotifier> logger) : IPushNotifier
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task SendAsync(UserId userId, PushPayload payload, TimeSpan timeToLive, CancellationToken ct = default)
    {
        List<UserPushSubscription> subs = [.. db.PushSubscriptions.Where(s => s.UserId == userId)];
        if (subs.Count == 0)
            return;

        PushServiceClient client = new(http) { DefaultAuthentication = vapid.Authentication };
        PushMessage message = new(JsonSerializer.Serialize(payload, JsonOpts))
        {
            Urgency = PushMessageUrgency.High,
            TimeToLive = (int)timeToLive.TotalSeconds,
        };

        List<UserPushSubscription> expired = [];
        foreach (UserPushSubscription s in subs)
        {
            PushSubscription target = new() { Endpoint = s.Endpoint };
            target.SetKey(PushEncryptionKeyName.P256DH, s.P256dh);
            target.SetKey(PushEncryptionKeyName.Auth, s.Auth);
            try
            {
                await client.RequestPushMessageDeliveryAsync(target, message, ct);
            }
            catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
            {
                expired.Add(s);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Push send failed for user {UserId}", userId);
            }
        }

        if (expired.Count > 0)
        {
            db.RemoveRange(expired);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// The server POSTs to whatever endpoint a browser registers, so an arbitrary URL would be
    /// a request-forgery hole into the host's network. Only the browsers' push services pass.
    /// </summary>
    public static bool IsPushServiceEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && KnownPushHosts.Any(h => uri.Host == h || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    // Chrome/Edge-on-Android (FCM), Firefox, Edge/Windows (WNS), Safari (APNs).
    private static readonly string[] KnownPushHosts =
        ["fcm.googleapis.com", "push.services.mozilla.com", "notify.windows.com", "push.apple.com"];
}
