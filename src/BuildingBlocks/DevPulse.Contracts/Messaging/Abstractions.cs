using DevPulse.Contracts.Events.Common;

namespace DevPulse.Contracts.Messaging;

/// <summary>
/// Dépose un événement à publier. Avec une base, il est écrit dans la table d'outbox et part avec la transaction
/// métier : jamais d'événement pour un changement annulé, jamais de changement validé sans son événement.
/// </summary>
public interface IOutbox
{
    void Enqueue(IntegrationEvent integrationEvent);
}

/// <summary>Traite un événement reçu. Peut être rappelé avec le même événement : l'infrastructure filtre les doublons quand le service a une base.</summary>
public interface IIntegrationEventHandler<in TEvent> where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken ct);
}

/// <summary>Files et liaisons, déclarées à l'identique par chaque service au démarrage (voir docs/events).</summary>
public static class EventTopology
{
    public const string Exchange = "devpulse.events";

    public const string IncidentServiceState = "incident.service-state";
    public const string IncidentAiAnalysis = "incident.ai-analysis";
    public const string AiIncidentCreated = "ai.incident-created";
    public const string AlertingIncidentEvents = "alerting.incident-events";
    public const string AlertingServiceStatus = "alerting.service-status";

    /// <summary>
    /// Liaisons explicites. « * » remplace exactement un mot en AMQP : « monitoring.service.* » ne correspondrait jamais
    /// à une clé en quatre segments, et « monitoring.service.# » ferait passer des événements que la file ne traite pas.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Bindings = new Dictionary<string, string[]>
    {
        [IncidentServiceState] = ["monitoring.service.down-detected.v1", "monitoring.service.recovered.v1"],
        [IncidentAiAnalysis] = ["ai.analysis.completed.v1"],
        [AiIncidentCreated] = ["incident.incident.created.v1"],
        [AlertingIncidentEvents] = ["incident.incident.*.v1"],
        [AlertingServiceStatus] = ["monitoring.service.status-changed.v1"],
    };
}
