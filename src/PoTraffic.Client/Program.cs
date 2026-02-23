using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PoTraffic.Client;
using PoTraffic.Client.Infrastructure.Auth;
using PoTraffic.Client.Infrastructure.Http;
using Radzen;

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Base address for API calls — same origin as the hosting ASP.NET Core app
Uri apiBase = new(builder.HostEnvironment.BaseAddress);

// AuthorizationMessageHandler attaches the JWT bearer token per-request and
// redirects to /login on 401 (Strategy pattern — DelegatingHandler chain).
builder.Services.AddScoped(sp =>
{
    var authProvider = sp.GetRequiredService<JwtAuthenticationStateProvider>();
    var nav = sp.GetRequiredService<NavigationManager>();
    var handler = new AuthorizationMessageHandler(authProvider, nav)
    {
        InnerHandler = new HttpClientHandler()
    };
    return new HttpClient(handler) { BaseAddress = apiBase };
});

// Authentication — JWT-based auth state provider backed by localStorage
builder.Services.AddScoped<JwtAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<JwtAuthenticationStateProvider>());
builder.Services.AddAuthorizationCore();

// Radzen component services (dialogs, tooltips, notifications, context menus)
builder.Services.AddRadzenComponents();

await builder.Build().RunAsync();
