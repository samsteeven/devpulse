using System.Diagnostics.Metrics;
using DevPulse.Alerting.Domain;

namespace DevPulse.Alerting.Infrastructure;

/// <summary>
/// Mesures des envois. Le délai de notification (du fait signalé à la réception par le destinataire) est l'objectif
/// chiffré du cahier des charges : moins de 10 s. Il est mesuré ici à chaque envoi réussi, pas seulement en test.
/// </summary>
public static class AlertingMetrics
{
    public const string MeterName = "DevPulse.Alerting";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Deliveries =
        Meter.CreateCounter<long>("devpulse.alerts.deliveries", "{delivery}", "Tentatives d'envoi, par résultat (sent, retry, failed)");

    private static readonly Histogram<double> NotificationLatency = Meter.CreateHistogram<double>(
        "devpulse.alerts.notification.latency", "ms", "Délai entre le fait signalé et l'accusé de réception du destinataire",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000, 60000, 300000] });

    public static void RecordAttempt(AlertDelivery delivery, ChannelKind kind)
    {
        var outcome = delivery.Status switch
        {
            DeliveryStatus.Sent => "sent",
            DeliveryStatus.Failed => "failed",
            _ => "retry",
        };
        Deliveries.Add(1,
            new KeyValuePair<string, object?>("kind", kind.ToString()),
            new KeyValuePair<string, object?>("event", delivery.Event.ToString()),
            new KeyValuePair<string, object?>("outcome", outcome));

        if (delivery.LatencyMilliseconds is { } latency)
            NotificationLatency.Record(latency, new KeyValuePair<string, object?>("event", delivery.Event.ToString()));
    }
}
