using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PoTraffic.Api.Features.Alerts.Entities;
using PoTraffic.Api.Infrastructure.Security;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.Alerts;

namespace PoTraffic.Api.Features.Alerts;

public static class AlertsEndpoints
{
    public static IEndpointRouteBuilder MapAlertsEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder alerts = app.MapGroup("/api/alerts")
            .RequireAuthorization("ProductionMicrosoftAuth").WithTags("Alerts");

        // GET /api/alerts?unreadOnly=true — recent alerts for the caller (#1)
        alerts.MapGet("", (HttpContext ctx, TableStorageContext db, [FromQuery] bool unreadOnly = false) =>
        {
            Guid userId = ctx.User.GetUserId();
            IEnumerable<Alert> q = db.Alerts.Where(a => a.UserId == userId);
            if (unreadOnly)
                q = q.Where(a => !a.IsRead);
            var list = q.OrderByDescending(a => a.CreatedAt).Take(50)
                .Select(AlertEvaluator.ToDto).ToList();
            return Results.Ok(list);
        });

        alerts.MapPost("{id:guid}/read", async (Guid id, HttpContext ctx, TableStorageContext db) =>
        {
            Guid userId = ctx.User.GetUserId();
            Alert? a = db.Alerts.FirstOrDefault(x => x.Id == id && x.UserId == userId);
            if (a is null) return Results.NotFound();
            a.IsRead = true;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        alerts.MapPost("read-all", async (HttpContext ctx, TableStorageContext db) =>
        {
            Guid userId = ctx.User.GetUserId();
            foreach (Alert a in db.Alerts.Where(x => x.UserId == userId && !x.IsRead).ToList())
                a.IsRead = true;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        RouteGroupBuilder push = app.MapGroup("/api/push")
            .RequireAuthorization("ProductionMicrosoftAuth").WithTags("Push");

        push.MapGet("vapid-public-key", (VapidKeyProvider vapid) =>
            Results.Ok(new VapidPublicKeyResponse(vapid.PublicKey)));

        push.MapPost("subscribe", async (
            HttpContext ctx, TableStorageContext db, [FromBody] PushSubscriptionRequest req) =>
        {
            Guid userId = ctx.User.GetUserId();
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
                    Id = Guid.NewGuid(),
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

        push.MapPost("unsubscribe", async (
            HttpContext ctx, TableStorageContext db, [FromBody] PushSubscriptionRequest req) =>
        {
            Guid userId = ctx.User.GetUserId();
            foreach (UserPushSubscription s in db.PushSubscriptions
                .Where(s => s.UserId == userId && s.Endpoint == req.Endpoint).ToList())
            {
                db.Remove(s);
            }
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}
