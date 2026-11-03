using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    /// Journaux JSON sur la console (avec TraceId et SpanId) ; traces, métriques et journaux envoyés en OTLP
    /// seulement si <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> est défini. Sans collecteur, le service démarre normalement.
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

        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(otlpEndpoint)) return builder;

        builder.Logging.AddOpenTelemetry(logs =>
        {
            logs.IncludeFormattedMessage = true;
            logs.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
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
                .AddMeter("System.Runtime"))
            .UseOtlpExporter();

        return builder;
    }
}
