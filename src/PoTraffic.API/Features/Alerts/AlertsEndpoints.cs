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

        return app;
    }
}
