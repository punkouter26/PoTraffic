using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PoTraffic.API.Features.Alerts;
using PoTraffic.API.Infrastructure.Security;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.Alerts;

namespace PoTraffic.API.Features.Alerts;

public static class AlertsEndpoints
{
    public static IEndpointRouteBuilder MapAlertsEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder alerts = app.MapGroup("/api/alerts")
            .RequireAuthorization("ProductionMicrosoftAuth").WithTags("Alerts");

        // GET /api/alerts?unreadOnly=true — recent alerts for the caller (#1)
        alerts.MapGet("", (HttpContext ctx, TableStorageContext db, [FromQuery] bool unreadOnly = false) =>
        {
            UserId userId = ctx.User.GetUserId();
            IEnumerable<Alert> q = db.Alerts.Where(a => a.UserId == userId);
            if (unreadOnly)
                q = q.Where(a => !a.IsRead);
            var list = q.OrderByDescending(a => a.CreatedAt).Take(50)
                .Select(AlertEvaluator.ToDto).ToList();
            return Results.Ok(list);
        });

        alerts.MapPost("{id:guid}/read", async (AlertId id, HttpContext ctx, TableStorageContext db) =>
        {
            UserId userId = ctx.User.GetUserId();
            Alert? a = db.Alerts.FirstOrDefault(x => x.Id == id && x.UserId == userId);
            if (a is null) return Results.NotFound();
            a.IsRead = true;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        alerts.MapPost("read-all", async (HttpContext ctx, TableStorageContext db) =>
        {
            UserId userId = ctx.User.GetUserId();
            foreach (Alert a in db.Alerts.Where(x => x.UserId == userId && !x.IsRead).ToList())
                a.IsRead = true;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        RouteGroupBuilder push = app.MapGroup("/api/push")
            .RequireAuthorization("ProductionMicrosoftAuth").WithTags("Push");

        // GET /api/push/key — the VAPID public key a browser subscribes with
        push.MapGet("key", (VapidKeys vapid) => Results.Ok(new PushKeyResponse(vapid.PublicKey)));

        // POST /api/push/subscriptions — register (or refresh the keys of) this browser
        push.MapPost("subscriptions", async (
            HttpContext ctx, TableStorageContext db, [FromBody] PushSubscriptionRequest req) =>
        {
            if (!WebPushNotifier.IsPushServiceEndpoint(req.Endpoint)
                || req.Endpoint.Length > 2048
                || string.IsNullOrWhiteSpace(req.P256dh) || req.P256dh.Length > 256
                || string.IsNullOrWhiteSpace(req.Auth) || req.Auth.Length > 256)
            {
                return Results.UnprocessableEntity(new { error = "INVALID_PUSH_SUBSCRIPTION" });
            }

            UserId userId = ctx.User.GetUserId();
            UserPushSubscription? existing = db.PushSubscriptions
                .FirstOrDefault(s => s.UserId == userId && s.Endpoint == req.Endpoint);
            if (existing is not null)
            {
                existing.P256dh = req.P256dh;
                existing.Auth = req.Auth;
            }
            else
            {
                db.Add(new UserPushSubscription
                {
                    Id = PushSubscriptionId.New(),
                    UserId = userId,
                    Endpoint = req.Endpoint,
                    P256dh = req.P256dh,
                    Auth = req.Auth,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // POST /api/push/unsubscribe — forget this browser
        push.MapPost("unsubscribe", async (
            HttpContext ctx, TableStorageContext db, [FromBody] PushUnsubscribeRequest req) =>
        {
            UserId userId = ctx.User.GetUserId();
            db.RemoveRange(db.PushSubscriptions
                .Where(s => s.UserId == userId && s.Endpoint == req.Endpoint).ToList());
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}

public sealed record PushKeyResponse(string PublicKey);
public sealed record PushSubscriptionRequest(string Endpoint, string P256dh, string Auth);
public sealed record PushUnsubscribeRequest(string Endpoint);
