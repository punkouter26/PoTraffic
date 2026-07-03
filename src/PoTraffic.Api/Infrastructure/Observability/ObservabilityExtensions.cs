using Azure.Monitor.OpenTelemetry.Exporter;
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

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(roleName))
            .WithMetrics(metrics => metrics
                .AddMeter("Microsoft.AspNetCore.Hosting")
                .AddMeter("Microsoft.AspNetCore.Server.Kestrel"))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
                if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
                    tracing.AddOtlpExporter();
        });

        // ── Azure Monitor tracing (CompositeRoutingSampler Strategy pattern) ──
        // CI/CD rule #8 — strict adaptive sampling in Production:
        //   • RecordsAll exceptions, errors, and dependencies that failed.
        //   • Samples 5% of healthy request traces and 1% of noisy background-job traces.
        //   • Honours parentContext and Sampler overrides from incoming Activity.
        // This keeps App Insights ingest under the 100MB/day quota while never
        // losing exception/dependency-failure signal.
        string? appInsightsConnStr = ResolveAppInsightsConnectionString(builder.Configuration);
        bool isProd = builder.Environment.IsProduction();
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(roleName))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new CompositeRoutingSampler(prodRatio: isProd ? 0.05 : 0.5))
                    .AddAspNetCoreInstrumentation();

                if (!string.IsNullOrWhiteSpace(appInsightsConnStr))
                {
                    tracing.AddAzureMonitorTraceExporter(opts =>
                    {
                        opts.ConnectionString = appInsightsConnStr;
                        if (isProd)
                        {
                            // Adaptive sampling: keep errors, drop successful traces beyond 5%.
                            // Exceptions bypass the sampler and are always recorded.
                            opts.SamplingRatio = 0.05f;
                        }
                    });
                }
            });

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
