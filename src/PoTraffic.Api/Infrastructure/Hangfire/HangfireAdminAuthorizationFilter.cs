using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;

namespace PoTraffic.Api.Infrastructure.Hangfire;

/// <summary>
/// Decorator pattern — wraps Hangfire dashboard access with Administrator role check.
/// FR-022: Hangfire dashboard MUST be restricted to users with the Administrator role.
/// </summary>
public sealed class HangfireAdminAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        HttpContext? httpContext = context.GetHttpContext();

        if (httpContext is null) return false;

        // Must be authenticated AND hold the Administrator role
        return httpContext.User.Identity?.IsAuthenticated == true
            && httpContext.User.IsInRole("Administrator");
    }
}
