using Azure.Monitor.OpenTelemetry.Exporter;
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
        // ── OpenTelemetry logs ────────────────────────────────────────────────
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("PoTraffic.Api"))
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
        string? appInsightsConnStr = builder.Configuration["ApplicationInsights:ConnectionString"];
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("PoTraffic.Api"))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new CompositeRoutingSampler())
                    .AddAspNetCoreInstrumentation()
                    .AddSource("Hangfire");

                if (!string.IsNullOrWhiteSpace(appInsightsConnStr))
                    tracing.AddAzureMonitorTraceExporter(opts => opts.ConnectionString = appInsightsConnStr);
            });

        // ── Serilog as sole MEL backend ───────────────────────────────────────
        builder.Host.UseSerilog((ctx, services, cfg) =>
        {
            cfg.ReadFrom.Configuration(ctx.Configuration)
               .ReadFrom.Services(services)
               .Enrich.FromLogContext();

            string? aiConnStr = ctx.Configuration["ApplicationInsights:ConnectionString"];
            if (!string.IsNullOrWhiteSpace(aiConnStr))
            {
                cfg.WriteTo.ApplicationInsights(
                    new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration { ConnectionString = aiConnStr },
                    TelemetryConverter.Traces);
            }
        });

        return builder;
    }
}
