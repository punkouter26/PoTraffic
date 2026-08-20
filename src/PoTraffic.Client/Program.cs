using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PoTraffic.Client;
using PoTraffic.Client.Infrastructure;
using PoTraffic.Client.Infrastructure.Auth;
using PoTraffic.Client.Infrastructure.Http;
using Radzen;

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Base address for API calls — same origin as the hosting ASP.NET Core app.
// The BFF session cookie flows automatically on same-origin requests; the
// client never stores or attaches tokens.
Uri apiBase = new(builder.HostEnvironment.BaseAddress);

builder.Services.AddScoped(sp => new HttpClient(
    new UnauthorizedRedirectHandler(sp.GetRequiredService<NavigationManager>())
    {
        InnerHandler = new HttpClientHandler()
    })
{ BaseAddress = apiBase });

// Authentication — cookie-backed BFF auth state provider (GET /api/auth/me)
builder.Services.AddScoped<CookieAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<CookieAuthenticationStateProvider>());
builder.Services.AddAuthorizationCore();

// Radzen component services (dialogs, tooltips, notifications, context menus)
builder.Services.AddRadzenComponents();

// PoTraffic native-browser interop (audio feedback, touch gestures)
builder.Services.AddScoped<PtInterop>();

// Last-known-good payloads, so the dashboard and the command palette paint before
// the first round-trip completes.
builder.Services.AddScoped<ClientCache>();

// One shared visibility/connectivity subscription driving every polling page.
builder.Services.AddScoped<PageActivityMonitor>();

// Holds deletes for a grace period so "Undo" has something to undo.
builder.Services.AddScoped<PendingDeletionService>();

// Service-worker registration, offline caching and the install prompt. Scoped rather
// than transient: the browser's beforeinstallprompt event fires once per page load and
// this is what holds it until Settings asks.
builder.Services.AddScoped<PwaService>();

// Visual effects and sound design: the background wash, the map's traffic flow, the
// celebration burst and the synthesised cue set, plus the settings that govern them.
builder.Services.AddScoped<FxService>();

await builder.Build().RunAsync();
