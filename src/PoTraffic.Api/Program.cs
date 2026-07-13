using FluentValidation;
using Microsoft.AspNetCore.HttpOverrides;
using PoTraffic.Api.Features.Account;
using PoTraffic.Api.Features.Admin;
using PoTraffic.Api.Features.Alerts;
using PoTraffic.Api.Features.Auth;
using PoTraffic.Api.Features.Config;
using PoTraffic.Api.Features.Diagnostics;
using PoTraffic.Api.Features.History;
using PoTraffic.Api.Features.Maintenance;
using PoTraffic.Api.Features.MonitoringWindows;
using PoTraffic.Api.Features.Places;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Api.Infrastructure;
using PoTraffic.Api.Infrastructure.Caching;
using PoTraffic.Api.Infrastructure.Logging;
using PoTraffic.Api.Infrastructure.Observability;
using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Api.Infrastructure.Scheduling;
using PoTraffic.Api.Infrastructure.Security;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Api.Infrastructure.Testing;
using Scalar.AspNetCore;
using Serilog;

// ── Serilog bootstrap (MEL-only; all app code uses ILogger<T>) ───────────────
// SelfLog flushes internal pipeline failures to stderr — in a 500.30 environment the
// MEL sink can stall before stdout flushes, so this is the last-resort "Serilog itself
// is broken, not the host" signal. Enable it the moment the bootstrap logger exists.
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

Serilog.Debugging.SelfLog.Enable(msg =>
    Console.Error.WriteLine($"[serilog-selflog] {DateTime.UtcNow:O} {msg}"));

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // Blazor WASM static web assets load automatically in Development and from published
    // output in Production; the Testing environment (E2E) must opt in explicitly.
    if (builder.Environment.IsEnvironment("Testing"))
        builder.WebHost.UseStaticWebAssets();

    // Key Vault must be added BEFORE service registrations — services read secrets
    // eagerly at registration time, so the vault has to be in the config pipeline first.
    builder.AddPoTrafficKeyVault();

    // ── Services (grouped by responsibility) ─────────────────────────────────
    builder.AddObservability();
    builder.Services.AddTableStoragePersistence();
    builder.Services.AddTableStorageServices(builder.Configuration, builder.Environment);
    builder.Services.AddBackgroundJobScheduler(builder.Environment);
    // Background job types — BackgroundSchedulerService resolves these from DI by the
    // type name stored with each scheduled job.
    builder.Services.AddScoped<PollRouteJob>();
    builder.Services.AddScoped<PruneOldPollRecordsJob>();
    builder.Services.AddScoped<TripleTestShotJob>();
    builder.Services.AddSecurityServices(builder.Configuration, builder.Environment.EnvironmentName);
    builder.Services.AddTrafficProviders(builder.Configuration, builder.Environment);
    builder.Services.AddAlertServices();
    builder.Services.AddPlacesServices();

    // Request dispatch (validation-first, see Infrastructure/Dispatch) + FluentValidation.
    builder.Services.AddDispatcher(typeof(Program).Assembly);
    builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

    // Hybrid cache (L1 in-proc, optional distributed L2). HTTP resilience pipelines are
    // registered per-client via ResiliencePipelineExtensions.AddResilienceHandler(name).
    builder.Services.AddPoTrafficHybridCache();

    // Problem Details (RFC 7807); GlobalExceptionHandler maps ValidationException → 422.
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    builder.Services.AddPoTrafficHealthChecks(builder.Environment, builder.Configuration);
    builder.Services.AddOpenApi();

    // Reverse-proxy awareness (Azure App Service): TLS terminates at the front end and the
    // app receives plain HTTP with the real scheme in X-Forwarded-Proto. Honoring it lets
    // UseHttpsRedirection see the true scheme (no redirect loop, no port-guess warning). The
    // proxy IP is not fixed, so clear the allowlist (App Service isolates the network).
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });

    WebApplication app = builder.Build();

    // Must run before HTTPS redirection / auth so downstream middleware sees the forwarded
    // scheme, host, and client IP rather than the proxy's.
    app.UseForwardedHeaders();

    // ── Hosted Blazor WASM static assets (must run BEFORE auth) ──────────────
    // UseBlazorFrameworkFiles serves /_framework/*; UseStaticFiles serves /css, /lib,
    // /_content, and the scoped-CSS bundle. Both are middleware, so they run ahead of the
    // authorization pipeline and are exempt from the deny-by-default FallbackPolicy (§4.5)
    // that guards the API — the WASM client must load before the user can sign in. Asset
    // fingerprinting is disabled in PoTraffic.Client.csproj so the physical file names match
    // the index.html references verbatim; no MapStaticAssets() endpoint routing is needed.
    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();

    // GlobalExceptionHandler handles ValidationException → 422; the rest fall through to the
    // default handler.
    app.UseExceptionHandler();

    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
        // Dev-only API docs — anonymous so they don't trip the deny-by-default fallback (§4.5).
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference().AllowAnonymous();
    }

    app.UseStatusCodePages();
    app.UseHttpsRedirection();
    app.UseSerilogRequestLogging();

    app.UseAuthentication();
    app.UseAuthorization();

    // Pushes UserId + Environment onto every Serilog/OTel log event across all sinks.
    app.UseMiddleware<LogContextEnrichmentMiddleware>();

    // ── API endpoints ─────────────────────────────────────────────────────────
    app.MapClientLogEndpoints();
    app.MapAccountEndpoints();
    app.MapAdminEndpoints();
    app.MapAuthEndpoints();
    app.MapDiagnosticsEndpoints();
    // GUEST login bypass: Development (Rule 4.4 split view) + Testing. Production registers
    // Microsoft OAuth only; MapGuestEndpoints throws if wired into a Production host.
    if (app.Environment.IsEnvironment("Testing") || app.Environment.IsDevelopment())
        app.MapGuestEndpoints(app.Environment);
    app.MapRoutesEndpoints();
    app.MapWindowsEndpoints();
    app.MapHistoryEndpoints();
    app.MapAlertsEndpoints();
    app.MapPlacesEndpoints();
    app.MapSystemEndpoints();
    app.MapDiagEndpoints();
    app.MapTestingEndpoints(app.Environment);

    // Error endpoint — anonymous so the exception handler re-execute path is never blocked
    // by the deny-by-default fallback (§4.5).
    app.MapGet("/error", () => Results.Problem()).ExcludeFromDescription().AllowAnonymous();

    app.MapPoTrafficHealthChecks();   // /health + /health/ready
    app.MapBlazorClientHost();        // unknown-/api 404, client-error sink, SPA fallback

    app.RunStartupTasks();            // background Table Storage hydration + nightly prune job

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Host terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}

// Marker so WebApplicationFactory<Program> can reference the host in integration tests.
public partial class Program { }
