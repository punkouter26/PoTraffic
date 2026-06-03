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

    // ── Pre-build SQL probe ───────────────────────────────────────────────────
    // In Development, attempt a fast TCP probe to the configured SQL Server.
    // If unreachable, set Hangfire:DisableServer=true so the worker thread
    // (which would otherwise crash on its first SQL connection) is not started.
    // This lets the app boot in "Table Storage only" dev mode.
    if (builder.Environment.IsDevelopment())
    {
        bool sqlAlive = ProbeSqlTcp(builder.Configuration);
        if (!sqlAlive)
        {
            Console.WriteLine(
                "[startup] SQL Server not reachable in Development. " +
                "Disabling Hangfire background server (jobs will be silently dropped). " +
                "App will boot in Table-Storage-only mode.");
            builder.Configuration["Hangfire:DisableServer"] = "true";
        }
    }

    // ── Infrastructure extension methods (grouped by responsibility) ──────────
    builder.AddObservability();
    builder.Services.AddTableStoragePersistence();
    builder.Services.AddTableStorageServices(builder.Configuration, builder.Environment);
    builder.Services.AddHangfireServices(builder.Configuration);
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
    string dashboardPath = app.Configuration["Hangfire:DashboardPath"] ?? "/hangfire";
    app.UseHangfireDashboard(dashboardPath, new DashboardOptions
    {
        Authorization = app.Environment.IsDevelopment()
            ? [new Hangfire.Dashboard.LocalRequestsOnlyAuthorizationFilter()]
            : [new HangfireAdminAuthorizationFilter()]
    });

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

    bool sqlReachable = await TryProbeSqlAsync(app);
    if (!sqlReachable && app.Environment.IsDevelopment())
    {
        // Dev-only: skip EF migrations + admin seed so the app boots in
        // "Table Storage only" mode. Endpoints that depend on the relational
        // store will return 500; /health will report SQL as Unhealthy.
        Console.WriteLine(
            "[startup] SQL Server not reachable in Development. " +
            "Skipping EF migrations and admin seed. " +
            "Endpoints requiring the relational store will return 500 until SQL is up.");
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

/// <summary>
/// Probes the SQL Server configured in <c>ConnectionStrings:Default</c> with a
/// quick <c>SELECT 1</c> query. Used during startup to decide whether to run
/// EF migrations and Hangfire schema installs, or to boot in a degraded
/// "Table Storage only" mode (Development only).
/// </summary>
static async Task<bool> TryProbeSqlAsync(WebApplication app)
{
    // Post-refactor: Table Storage is always available locally via Azurite.
    // This probe is retained for any future backend switch; for now it always
    // returns false (SQL is not in the architecture) so the host stays in
    // Table-Storage-only mode.
    await Task.CompletedTask;
    return false;
}

/// <summary>
/// Lightweight TCP probe used BEFORE the host is built. Opens a TCP socket to
/// the host:port parsed from <c>ConnectionStrings:Default</c> and immediately
/// closes it. Returns <c>true</c> on success, <c>false</c> on any error or
/// when the connection string is missing. Used in Development to decide
/// whether to enable the Hangfire background server.
/// </summary>
static bool ProbeSqlTcp(IConfiguration configuration)
{
    try
    {
        string? cs = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(cs)) return false;

        var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = cs };
        string? server = builder["Server"] as string ?? builder["Data Source"] as string;
        if (string.IsNullOrWhiteSpace(server)) return false;

        // Strip optional instance name (e.g. "host\instance") — we only probe the host:port
        string hostPart = server.Contains(',') ? server.Split(',')[0] : server;
        string[] hostPort = hostPart.Split(':');
        string host = hostPort[0];
        int port = hostPort.Length > 1 && int.TryParse(hostPort[1], out int p) ? p : 1433;

        using var client = new System.Net.Sockets.TcpClient();
        var task = client.ConnectAsync(host, port);
        if (!task.Wait(TimeSpan.FromSeconds(2))) return false;
        return client.Connected;
    }
    catch
    {
        return false;
    }
}

// Marker for WebApplicationFactory<Program> in integration tests
public partial class Program { }
