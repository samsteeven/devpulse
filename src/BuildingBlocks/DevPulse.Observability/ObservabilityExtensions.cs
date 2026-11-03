using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Azure.Monitor.OpenTelemetry.Exporter;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace DevPulse.Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// Journaux JSON sur la console (avec TraceId et SpanId). Traces, métriques et journaux partent vers un collecteur
    /// OTLP si <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> est défini (Grafana en local), vers Application Insights si
    /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> l'est (Azure), ou les deux. Sans l'un ni l'autre, le service
    /// démarre normalement et n'exporte rien.
    /// </summary>
    public static TBuilder AddDevPulseObservability<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        // Sans cela, le fournisseur de console par défaut écrit chaque ligne une seconde fois, en texte.
        builder.Logging.ClearProviders();

        builder.Services.AddSerilog(
            logger => logger
                .ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("service", serviceName)
                .WriteTo.Console(new RenderedCompactJsonFormatter()),
            writeToProviders: true);

        var otlp = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var appInsights = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        var azure = !string.IsNullOrWhiteSpace(appInsights);
        if (!otlp && !azure) return builder;

        builder.Logging.AddOpenTelemetry(logs =>
        {
            logs.IncludeFormattedMessage = true;
            logs.IncludeScopes = true;
            if (azure) logs.AddAzureMonitorLogExporter(o => o.ConnectionString = appInsights);
        });

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddSource("DevPulse.Messaging"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("System.Runtime")
                // Mesures métier : DevPulse.Messaging, .Monitoring, .Incident, .Alerting.
                .AddMeter("DevPulse.*"));

        if (otlp) telemetry.UseOtlpExporter();
        if (azure)
        {
            telemetry
                .WithTracing(tracing => tracing.AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsights))
                .WithMetrics(metrics => metrics.AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsights));
        }

        return builder;
    }
}
