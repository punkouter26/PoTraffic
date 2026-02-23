using System.Text;
using System.Security.Claims;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using FluentValidation;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PoTraffic.Api.Features.Account;
using PoTraffic.Api.Features.Admin;
using PoTraffic.Api.Features.Auth;
using PoTraffic.Api.Features.History;
using PoTraffic.Api.Features.Maintenance;
using PoTraffic.Api.Features.MonitoringWindows;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Api.Features.Config;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Api.Infrastructure;
using PoTraffic.Api.Infrastructure.Hangfire;
using PoTraffic.Api.Infrastructure.Logging;
using PoTraffic.Api.Infrastructure.Observability;
using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Api.Infrastructure.Security;
using PoTraffic.Api.Infrastructure.Testing;
using PoTraffic.Shared.Enums;
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

    // ── Aspire service defaults (OTLP exporter, service discovery, HTTP resilience) ─
    builder.AddServiceDefaults();

    // ── Serilog as sole MEL backend (Amendment v1.1.0) ───────────────────────
    builder.Host.UseSerilog((ctx, services, cfg) =>
        cfg.ReadFrom.Configuration(ctx.Configuration)
           .ReadFrom.Services(services)
           .Enrich.FromLogContext());

    // ── Azure Key Vault (optional; skipped when VaultUri is empty) ───────────
    string? vaultUri = builder.Configuration["AzureKeyVault:VaultUri"];
    if (!string.IsNullOrWhiteSpace(vaultUri))
    {
        builder.Configuration.AddAzureKeyVault(
            new Uri(vaultUri),
            new DefaultAzureCredential(),
            new KeyVaultSecretManager());
    }

    // ── Typed options ─────────────────────────────────────────────────────────
    builder.Services.Configure<JwtConfiguration>(
        builder.Configuration.GetSection("Jwt"));
    JwtConfiguration jwtCfg = builder.Configuration.GetSection("Jwt").Get<JwtConfiguration>()
        ?? throw new InvalidOperationException("Jwt configuration section is missing.");

    // ── EF Core ───────────────────────────────────────────────────────────────
    string? connectionString = builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Default connection string is missing.");

    builder.Services.AddDbContext<PoTrafficDbContext>(options =>
        options.UseSqlServer(
            connectionString,
            sql => sql.EnableRetryOnFailure(maxRetryCount: 5)));

    // ── Hangfire (Amendment v1.1.0) ───────────────────────────────────────────
    builder.Services.AddHangfire(cfg => cfg
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(
            connectionString,
            new SqlServerStorageOptions
            {
                CommandBatchMaxTimeout      = TimeSpan.FromMinutes(5),
                SlidingInvisibilityTimeout  = TimeSpan.FromMinutes(5),
                QueuePollInterval           = TimeSpan.Zero,
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks           = true
            }));

    // Adapter pattern — HangfireJobActivator bridges Hangfire job activation to ASP.NET Core DI scope lifecycle
    builder.Services.AddHangfireServer((sp, options) =>
        options.Activator = new HangfireJobActivator(sp.GetRequiredService<IServiceScopeFactory>()));

    // Register Hangfire job classes in DI so HangfireJobActivator can resolve them
    builder.Services.AddScoped<PollRouteJob>();
    builder.Services.AddScoped<TripleTestShotJob>();
    builder.Services.AddScoped<PruneOldPollRecordsJob>();

    // ── MediatR CQRS ─────────────────────────────────────────────────────────
    builder.Services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
        // Pipeline Behavior pattern — ValidationBehavior runs FluentValidation
        // validators before every handler, decoupling validation from handlers.
        cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
    });

    // ── FluentValidation ──────────────────────────────────────────────────────
    // Validators are registered; ValidationBehavior<,> invokes them via the MediatR pipeline.
    builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

    // ── Authentication / JWT Bearer ───────────────────────────────────────────
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer           = true,
                ValidateAudience         = true,
                ValidateLifetime         = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer              = jwtCfg.Issuer,
                ValidAudience            = jwtCfg.Audience,
                IssuerSigningKey         = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(jwtCfg.Key)),
                ClockSkew = TimeSpan.Zero,
                // Ensure "sub" and "role" claims are mapped correctly to User.Identity properties
                NameClaimType = "sub",
                RoleClaimType = ClaimTypes.Role
            };
        });

    builder.Services.AddAuthorization(opts =>
    {
        opts.AddPolicy("AdminOnly", p => p.RequireRole("Administrator"));
    });

    // JWT token service for auth handlers
    builder.Services.AddSingleton<JwtTokenService>();

    // ── OpenTelemetry + CompositeRoutingSampler (Strategy pattern) ────────────
    string? appInsightsConnStr = builder.Configuration["ApplicationInsights:ConnectionString"];
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("PoTraffic.Api"))
        .WithTracing(tracing =>
        {
            tracing
                .SetSampler(new CompositeRoutingSampler())
                .AddAspNetCoreInstrumentation()
                .AddSource("Hangfire");

            // Only wire Azure Monitor when a connection string is present (skipped in local dev)
            if (!string.IsNullOrWhiteSpace(appInsightsConnStr))
                tracing.AddAzureMonitorTraceExporter(opts => opts.ConnectionString = appInsightsConnStr);
        });

    // ── CORS (allow WASM client in development) ───────────────────────────────
    builder.Services.AddCors(opts =>
        opts.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

    // ── Traffic providers — Strategy pattern, keyed by RouteProvider enum ────
    if (builder.Environment.IsEnvironment("Testing"))
    {
        // Integration/E2E isolation — replace production providers with mocks
        // Strategy pattern — swapping implementations per environment
        builder.Services.AddKeyedScoped<ITrafficProvider, MockTrafficProvider>(RouteProvider.GoogleMaps);
        builder.Services.AddKeyedScoped<ITrafficProvider, MockTrafficProvider>(RouteProvider.TomTom);
    }
    else
    {
        builder.Services.AddHttpClient<GoogleMapsTrafficProvider>();
        builder.Services.AddHttpClient<TomTomTrafficProvider>();
        builder.Services.AddKeyedScoped<ITrafficProvider, GoogleMapsTrafficProvider>(RouteProvider.GoogleMaps);
        builder.Services.AddKeyedScoped<ITrafficProvider, TomTomTrafficProvider>(RouteProvider.TomTom);
    }
    // Factory pattern — ITrafficProviderFactory hides IKeyedServiceProvider cast from handlers
    builder.Services.AddScoped<ITrafficProviderFactory, KeyedServiceTrafficProviderFactory>();

    // ── Problem Details (RFC 7807) ────────────────────────────────────────────
    builder.Services.AddProblemDetails();
    // Chain of Responsibility pattern — GlobalExceptionHandler maps ValidationException → 422
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    WebApplication app = builder.Build();

    // ── Exception handling ────────────────────────────────────────────────────
    // GlobalExceptionHandler runs first (handles ValidationException → 422);
    // unhandled exceptions fall through to the default handler.
    app.UseExceptionHandler();

    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }

    app.UseStatusCodePages();

    // ── Security headers / HTTPS ─────────────────────────────────────────────
    app.UseHttpsRedirection();
    app.UseCors();
    app.UseSerilogRequestLogging();

    // ── Auth middleware ───────────────────────────────────────────────────────
    app.UseAuthentication();
    app.UseAuthorization();

    // ── Hangfire dashboard ────────────────────────────────────────────────────
    string dashboardPath = app.Configuration["Hangfire:DashboardPath"] ?? "/hangfire";
    app.UseHangfireDashboard(dashboardPath, new DashboardOptions
    {
        // T111: HangfireAdminAuthorizationFilter restricts dashboard to Administrator role
        // Decorator pattern — wraps dashboard access with role check
        Authorization = app.Environment.IsDevelopment()
            ? [new Hangfire.Dashboard.LocalRequestsOnlyAuthorizationFilter()]
            : [new HangfireAdminAuthorizationFilter()]
    });

    // ── API endpoints ─────────────────────────────────────────────────────────
    app.MapClientLogEndpoints();
    app.MapAccountEndpoints();
    app.MapAdminEndpoints();
    app.MapAuthEndpoints();
    app.MapRoutesEndpoints();
    app.MapWindowsEndpoints();
    app.MapHistoryEndpoints();
    app.MapSystemEndpoints();
    app.MapTestingEndpoints(app.Environment);

    // Error endpoint
    app.MapGet("/error", () => Results.Problem()).ExcludeFromDescription();

    // Placeholder health-check
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
       .WithName("HealthCheck")
       .AllowAnonymous();

    // ── Serve Blazor WASM fallback (non-API requests) ─────────────────────────
    app.MapStaticAssets();
    app.MapFallbackToFile("index.html");

    // ── Startup: run EF Core migrations and seed admin user ─────────────────
    // Ensures schema is always current and an Administrator account exists on
    // every cold-start (idempotent — safe to run against an existing database).
    await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
    {
        PoTrafficDbContext db = scope.ServiceProvider.GetRequiredService<PoTrafficDbContext>();
        ILogger<Program> startupLog = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        await db.Database.MigrateAsync();
        startupLog.LogInformation("Database migrations applied.");

        const string adminEmail    = "admin@potraffic.dev";
        const string adminPassword = "Admin123!";

        bool adminExists = await db.Set<User>()
            .AnyAsync(u => u.Email == adminEmail);

        if (!adminExists)
        {
            db.Set<User>().Add(
                new User
                {
                    Id                     = Guid.NewGuid(),
                    Email                  = adminEmail,
                    PasswordHash           = BCrypt.Net.BCrypt.HashPassword(adminPassword),
                    Locale                 = "en-US",
                    Role                   = "Administrator",
                    IsEmailVerified        = true,
                    EmailVerificationToken = null,
                    CreatedAt              = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
            startupLog.LogInformation("Default admin user created ({Email}).", adminEmail);
        }
    }

    // T086 — Register nightly pruning recurring job (02:00 UTC)
    // Template Method pattern — Hangfire invokes ExecuteAsync() on schedule
    RecurringJob.AddOrUpdate<PruneOldPollRecordsJob>(
        "prune-old-poll-records",
        job => job.ExecuteAsync(),
        "0 2 * * *");

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
