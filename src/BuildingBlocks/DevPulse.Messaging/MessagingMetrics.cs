using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace DevPulse.Messaging;

/// <summary>
/// Mesures de la messagerie. Ce sont celles qui disent si la plateforme tient ses promesses : un événement qui
/// n'arrive pas, une file morte qui se remplit ou une outbox qui vieillit ne se voient dans aucune route HTTP.
/// </summary>
public static class MessagingMetrics
{
    public const string MeterName = "DevPulse.Messaging";

    private static readonly Meter Meter = new(MeterName);

    internal static readonly Counter<long> Published =
        Meter.CreateCounter<long>("devpulse.messaging.published", "{event}", "Événements confirmés par le courtier");

    internal static readonly Counter<long> PublishFailures =
        Meter.CreateCounter<long>("devpulse.messaging.publish.failures", "{event}", "Publications refusées ou sans réponse du courtier");

    /// <summary>Étiquette <c>outcome</c> : processed, duplicate, retried, dead_lettered.</summary>
    internal static readonly Counter<long> Consumed =
        Meter.CreateCounter<long>("devpulse.messaging.consumed", "{message}", "Messages reçus, par résultat");

    internal static readonly Histogram<double> ProcessingDuration =
        Meter.CreateHistogram<double>("devpulse.messaging.processing.duration", "ms", "Durée du traitement d'un message");

    private static readonly ConcurrentDictionary<string, (long Pending, double OldestAgeSeconds)> Outboxes = new();

    static MessagingMetrics()
    {
        Meter.CreateObservableGauge("devpulse.outbox.pending",
            () => Outboxes.Select(o => new Measurement<long>(o.Value.Pending, new KeyValuePair<string, object?>("store", o.Key))),
            "{event}", "Événements écrits mais pas encore publiés");
        Meter.CreateObservableGauge("devpulse.outbox.oldest.age",
            () => Outboxes.Select(o => new Measurement<double>(o.Value.OldestAgeSeconds, new KeyValuePair<string, object?>("store", o.Key))),
            "s", "Âge du plus ancien événement non publié : il grandit si le courtier est absent");
    }

    internal static void ReportOutbox(string store, long pending, double oldestAgeSeconds) =>
        Outboxes[store] = (pending, oldestAgeSeconds);

    internal static KeyValuePair<string, object?> Tag(string name, object? value) => new(name, value);
}
