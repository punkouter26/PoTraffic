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

await builder.Build().RunAsync();
