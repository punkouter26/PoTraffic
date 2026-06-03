using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Data.Tables;
using Azure.Identity;
using FluentValidation;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoTraffic.Api.Features.Account;
using PoTraffic.Api.Features.Admin;
using PoTraffic.Api.Features.Auth;
using PoTraffic.Api.Features.Config;
using PoTraffic.Api.Features.History;
using PoTraffic.Api.Features.Maintenance;
using PoTraffic.Api.Features.MonitoringWindows;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Api.Infrastructure;
using PoTraffic.Api.Infrastructure.Hangfire;
using PoTraffic.Api.Infrastructure.Logging;
using PoTraffic.Api.Infrastructure.Observability;
using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Api.Infrastructure.Security;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Api.Infrastructure.Testing;
using Scalar.AspNetCore;
using Serilog;

// ── Serilog bootstrap (MEL-only; all app code uses ILogger<T>) ───────────────
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // ── Static web assets in Testing environment ──────────────────────────────
    // Blazor WASM static web assets are only loaded automatically in Development
    // and from published output in Production. For the Testing environment (E2E)
    // we must explicitly opt-in so the Blazor client is served correctly.
    if (builder.Environment.IsEnvironment("Testing"))
        builder.WebHost.UseStaticWebAssets();

    // ── Azure Key Vault (must be added BEFORE service registrations) ─────────
    // Services read configuration eagerly at registration time (e.g. JWT signing key).
    // Key Vault must be in the configuration pipeline first so that all services receive
    // the resolved secrets rather than the appsettings.json placeholder values.
    // PrefixKeyVaultSecretManager strips the "PoTraffic--" namespace prefix so that
    // e.g. "PoTraffic--ConnectionStrings--Default" → config key "ConnectionStrings:Default".
    //
    // Rule 10 (First-Run Success): In Development we try Key Vault first when configured,
    // but if `DefaultAzureCredential` cannot acquire a token (no `az login` available)
    // we fall back to appsettings.Development.json silently so a fresh checkout still boots.
    // In Production/Staging a Key Vault failure is treated as a hard error — secrets
    // MUST come from Key Vault, never from a checked-in appsettings file.
    string? vaultUri = builder.Configuration["KeyVault:Uri"];
    bool keyVaultConfigured = !string.IsNullOrWhiteSpace(vaultUri);
    bool isDev = builder.Environment.IsDevelopment();
    if (keyVaultConfigured)
    {
        if (isDev)
        {
            // Chain of Responsibility — try the credential, swallow CredentialUnavailableException
            // so first-time contributors without `az login` can still `dotnet run` locally.
            try
            {
                builder.Configuration.AddAzureKeyVault(
                    new Uri(vaultUri!),
                    new DefaultAzureCredential(),
                    new PrefixKeyVaultSecretManager());
            }
            catch (Azure.Identity.CredentialUnavailableException ex)
            {
                Console.WriteLine(
                    $"[startup] Key Vault unreachable ({ex.GetType().Name}); " +
                    "falling back to appsettings.Development.json (DEV-ONLY). " +
                    "Run `az login` to load secrets from Key Vault.");
            }
        }
        else
        {
            // Production / Staging — let exceptions propagate. Rule 6 + 13.
            builder.Configuration.AddAzureKeyVault(
                new Uri(vaultUri!),
                new DefaultAzureCredential(),
                new PrefixKeyVaultSecretManager());
        }
    }

    // Guard: a placeholder JWT key in Production is a security incident waiting
    // to happen. Fail-fast so it never reaches the wire.
    string? jwtKey = builder.Configuration["Jwt:Key"];
    bool isProdLike = builder.Environment.IsProduction() || builder.Environment.IsStaging();
    bool jwtKeyLooksPlaceholder = string.IsNullOrWhiteSpace(jwtKey)
        || jwtKey.StartsWith("REPLACE_WITH", StringComparison.OrdinalIgnoreCase)
        || jwtKey.StartsWith("PoTraffic-LocalDev", StringComparison.OrdinalIgnoreCase);
    if (isProdLike && (jwtKeyLooksPlaceholder || !keyVaultConfigured))
    {
        throw new InvalidOperationException(
            "JWT signing key is missing or still a placeholder, and Key Vault is not configured. " +
            "Production requires 'KeyVault:Uri' set and a non-placeholder 'Jwt:Key' resolved from Key Vault.");
    }

    // ── Infrastructure extension methods (grouped by responsibility) ──────────
    builder.AddObservability();
    builder.Services.AddTableStoragePersistence();
    builder.Services.AddTableStorageServices(builder.Configuration, builder.Environment);
    builder.Services.AddHangfireServices(builder.Configuration, builder.Environment);
    builder.Services.AddSecurityServices(builder.Configuration, builder.Environment.EnvironmentName);
    builder.Services.AddTrafficProviders(builder.Configuration, builder.Environment);

    // ── MediatR CQRS ─────────────────────────────────────────────────────────
    builder.Services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
        // Pipeline Behavior pattern — ValidationBehavior runs FluentValidation
        // validators before every handler, decoupling validation from handlers.
        cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
    });

    // ── FluentValidation ──────────────────────────────────────────────────────
    builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

    // ── CORS (allow WASM client in development) ───────────────────────────────
    builder.Services.AddCors(opts =>
        opts.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

    // ── HTTP client resilience defaults ───────────────────────────────────────
    builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());

    // ── Problem Details (RFC 7807) ────────────────────────────────────────────
    builder.Services.AddProblemDetails();
    // Chain of Responsibility pattern — GlobalExceptionHandler maps ValidationException → 422
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    // ── Health checks ──────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks();

    // ── OpenAPI (Scalar UI) ───────────────────────────────────────────────────
    builder.Services.AddOpenApi();

    WebApplication app = builder.Build();

    // ── Blazor WASM WebRootFileProvider fix ───────────────────────────────────
    // In .NET 10, MapStaticAssets() creates endpoint-based routes for each fingerprinted
    // static asset, but MapFallbackToFile("index.html") uses WebRootFileProvider (the older
    // middleware approach). UseStaticWebAssets() may not update WebRootFileProvider correctly
    // outside of the Development environment. We manually populate it from the runtime manifest
    // so that SPA routing (MapFallbackToFile) can serve index.html for all non-API routes.
    if (!app.Environment.IsProduction())
    {
        IWebHostEnvironment webEnv = app.Services.GetRequiredService<IWebHostEnvironment>();
        ILogger<Program> startupLogger = app.Services.GetRequiredService<ILogger<Program>>();

        if (!webEnv.WebRootFileProvider.GetFileInfo("index.html").Exists)
        {
            startupLogger.LogWarning(
                "WebRootFileProvider does not contain index.html — configuring from static web assets manifest.");

            string manifestPath = Path.Combine(
                AppContext.BaseDirectory,
                $"{webEnv.ApplicationName}.staticwebassets.runtime.json");

            if (File.Exists(manifestPath))
            {
                using System.Text.Json.JsonDocument manifestDoc =
                    System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (manifestDoc.RootElement.TryGetProperty("ContentRoots",
                    out System.Text.Json.JsonElement roots))
                {
                    Microsoft.Extensions.FileProviders.IFileProvider[] providers = roots.EnumerateArray()
                        .Select(r => r.GetString() ?? string.Empty)
                        .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
                        .Select(p =>
                            (Microsoft.Extensions.FileProviders.IFileProvider)
                            new Microsoft.Extensions.FileProviders.PhysicalFileProvider(p))
                        .ToArray();

                    if (providers.Length > 0)
                    {
                        webEnv.WebRootFileProvider =
                            new Microsoft.Extensions.FileProviders.CompositeFileProvider(providers);
                        startupLogger.LogInformation(
                            "Configured WebRootFileProvider with {Count} content root(s) from static web assets manifest.",
                            providers.Length);
                    }
                }
            }
            else
            {
                startupLogger.LogWarning("Static web assets manifest not found at {Path}.", manifestPath);
            }
        }
    }

    // ── Exception handling ────────────────────────────────────────────────────
    // GlobalExceptionHandler runs first (handles ValidationException → 422);
    // unhandled exceptions fall through to the default handler.
    app.UseExceptionHandler();

    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
        // OpenAPI document at /openapi/v1.json; Scalar UI at /scalar/v1
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    app.UseStatusCodePages();

    // ── Security headers / HTTPS ─────────────────────────────────────────────
    app.UseHttpsRedirection();
    app.UseCors();
    app.UseSerilogRequestLogging();

    // ── Auth middleware ───────────────────────────────────────────────────────
    app.UseAuthentication();
    app.UseAuthorization();

    // ── Log context enrichment — pushes UserId and Environment into every log event ──
    // Ensures Serilog + OTel log entries carry these properties in all sinks.
    app.UseMiddleware<LogContextEnrichmentMiddleware>();

    // ── Hangfire dashboard ────────────────────────────────────────────────────
    // T111: HangfireAdminAuthorizationFilter restricts dashboard to Administrator role
    // Decorator pattern — wraps dashboard access with role check
    // Skip in Testing where SQL Server storage is not configured.
    if (!app.Environment.IsEnvironment("Testing"))
    {
        string dashboardPath = app.Configuration["Hangfire:DashboardPath"] ?? "/hangfire";
        app.UseHangfireDashboard(dashboardPath, new DashboardOptions
        {
            Authorization = app.Environment.IsDevelopment()
                ? [new Hangfire.Dashboard.LocalRequestsOnlyAuthorizationFilter()]
                : [new HangfireAdminAuthorizationFilter()]
        });
    }

    // ── API endpoints ─────────────────────────────────────────────────────────
    app.MapClientLogEndpoints();
    app.MapAccountEndpoints();
    app.MapAdminEndpoints();
    app.MapAuthEndpoints();
    // Rule 6 / Rule 13: GUEST login is only registered in Dev + Testing.
    // In Production the endpoint is omitted entirely (no `guest-login` route).
    if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
    {
        app.MapGuestEndpoints();
    }
    app.MapRoutesEndpoints();
    app.MapWindowsEndpoints();
    app.MapHistoryEndpoints();
    app.MapSystemEndpoints();
    app.MapDiagEndpoints();
    app.MapTestingEndpoints(app.Environment);

    // Error endpoint
    app.MapGet("/error", () => Results.Problem()).ExcludeFromDescription();

    // Health check endpoint — pings DB and external APIs, returns JSON status per dependency
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = async (httpCtx, report) =>
        {
            httpCtx.Response.ContentType = "application/json";
            string result = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = report.Status.ToString(),
                entries = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    durationMs = e.Value.Duration.TotalMilliseconds
                })
            });
            await httpCtx.Response.WriteAsync(result);
        }
    }).AllowAnonymous();

    // ── Serve Blazor WASM fallback (non-API requests) ─────────────────────────
    app.MapStaticAssets();

    if (app.Environment.IsEnvironment("Testing"))
    {
        string testingIndexPath = Path.GetFullPath(
            Path.Combine(app.Environment.ContentRootPath, "..", "PoTraffic.Client", "wwwroot", "index.html"));

        if (File.Exists(testingIndexPath))
        {
            app.MapGet("/index.html", () => Results.File(testingIndexPath, "text/html"));
            app.MapFallback(() => Results.File(testingIndexPath, "text/html"));
        }
        else
        {
            app.MapFallbackToFile("index.html");
        }
    }
    else
    {
        app.MapFallbackToFile("index.html");
    }

    // ── Startup: run EF Core migrations, seed admin user, ensure Table Storage tables ──
    // Ensures schema is always current and an Administrator account exists on
    // every cold-start (idempotent — safe to run against an existing database).
    // Retry loop handles Azure SQL serverless auto-pause resume latency (can take
    // 30-90s on cold wake). Without retries, MigrateAsync() times out and crashes
    // the app before it can serve requests.
    //
    // Dev-only: if SQL is unreachable, we skip migrations + admin seed and let
    // the app boot in a degraded "Table Storage only" mode. The /health and
    // /diag endpoints will reflect the missing SQL dependency.
    // ── Ensure Table Storage tables exist (idempotent, Azurite + Azure) ────
    {
        TableServiceClient? tableService = app.Services.GetService<TableServiceClient>();
        if (tableService is not null)
        {
            try
            {
                await TableStorageExtensions.EnsureTablesExistAsync(tableService, "TrafficPolls");
                Console.WriteLine("[startup] Table Storage: TrafficPolls table ensured.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[startup] Table Storage: failed to ensure TrafficPolls table — continuing. {ex.Message}");
            }
        }
    }

    // Post-refactor: SQL is gone from the architecture. The default admin seed and
    // configuration rows live in Table Storage; the in-memory TableStorageContext
    // is pre-seeded with default SystemConfiguration rows by SeedDefaultConfigurationsIfMissing().
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    TableStorageContext db = scope.ServiceProvider.GetRequiredService<TableStorageContext>();
    db.SeedDefaultConfigurationsIfMissing();

    // T086 — Register nightly pruning recurring job (02:00 UTC).
    // Post-refactor: pruning now operates on the in-memory poll list; no
    // Hangfire SQL backend is required, so this runs unconditionally.
    if (app.Services.GetService<IBackgroundJobClient>() is not null)
    {
        RecurringJob.AddOrUpdate<PruneOldPollRecordsJob>(
            "prune-old-poll-records",
            job => job.ExecuteAsync(),
            "0 2 * * *");
    }

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

// Marker for WebApplicationFactory<Program> in integration tests
public partial class Program { }
