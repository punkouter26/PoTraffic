using Azure.Monitor.OpenTelemetry.AspNetCore;
using System.Reflection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace PoTraffic.Api.Infrastructure.Observability;

internal static class ObservabilityExtensions
{
    /// <summary>
    /// Registers OpenTelemetry (OTLP + Azure Monitor) and wires Serilog as the MEL backend.
    /// </summary>
    internal static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        string roleName = Assembly.GetExecutingAssembly().GetName().Name ?? "PoTraffic.Api";

        // ── OpenTelemetry logs ────────────────────────────────────────────────
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        // ── OpenTelemetry pipeline (§6.3) ─────────────────────────────────────
        // Head sampling is owned solely by CompositeRoutingSampler:
        //   • Dev / Test → 100 % capture.
        //   • Prod → rate-limited (10 healthy + 1 job trace per second), error/parent bypass.
        // When an App Insights connection string is present we use the Azure Monitor distro
        // so Live Metrics is active and the AI exporters + instrumentation are wired for us;
        // its default sampler is then overridden by CompositeRoutingSampler.
        string? appInsightsConnStr = ResolveAppInsightsConnectionString(builder.Configuration);
        bool isProd = builder.Environment.IsProduction();

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(roleName))
            .WithMetrics(metrics => metrics
                .AddMeter("Microsoft.AspNetCore.Hosting")
                .AddMeter("Microsoft.AspNetCore.Server.Kestrel"));

        if (!string.IsNullOrWhiteSpace(appInsightsConnStr))
        {
            otel.UseAzureMonitor(o =>
            {
                o.ConnectionString = appInsightsConnStr;
                o.EnableLiveMetrics = true;       // §6.3 — Live Metrics stays active.
                o.SamplingRatio = 1.0f;           // don't re-drop what the head sampler kept.
            });
            otel.WithTracing(tracing =>
            {
                tracing.SetSampler(new CompositeRoutingSampler(isProd));
                if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
                    tracing.AddOtlpExporter();
            });
        }
        else
        {
            otel.WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new CompositeRoutingSampler(isProd))
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
                if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
                    tracing.AddOtlpExporter();
            });
        }

        // ── Serilog as sole MEL backend ───────────────────────────────────────
        builder.Host.UseSerilog((ctx, services, cfg) =>
        {
            cfg.ReadFrom.Configuration(ctx.Configuration)
               .ReadFrom.Services(services)
               .Enrich.FromLogContext();
            // AppInsights telemetry handled by AddAzureMonitorTraceExporter() in OTel pipeline.
        });

        return builder;
    }

    private static string? ResolveAppInsightsConnectionString(IConfiguration configuration)
    {
        string? connectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
            ?? configuration["ApplicationInsights:ConnectionString"];

        if (!string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        string? instrumentationKey = configuration["APPINSIGHTS_INSTRUMENTATIONKEY"]
            ?? configuration["ApplicationInsights:InstrumentationKey"];

        if (!string.IsNullOrWhiteSpace(instrumentationKey))
            return $"InstrumentationKey={instrumentationKey}";

        return configuration["ApplicationInsights:StagingFallbackConnectionString"];
    }
}
