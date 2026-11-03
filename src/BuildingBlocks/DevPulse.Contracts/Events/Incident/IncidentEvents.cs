using DevPulse.Contracts.Events.Common;

namespace DevPulse.Contracts.Events.Incident;

/// <summary>Champs communs : un consommateur (alertes, temps réel) n'a pas à rappeler Incident pour afficher l'essentiel.</summary>
public abstract record IncidentEventBase : IntegrationEvent
{
    public required Guid IncidentId { get; init; }
    public required Guid ServiceId { get; init; }
    public required string ServiceName { get; init; }
    public required string Environment { get; init; }
    public required string Severity { get; init; }
    public required string Title { get; init; }
}

public sealed record IncidentCreatedV1 : IncidentEventBase
{
    public override string EventType => "incident.incident.created";
    public override int Version => 1;

    public required string Source { get; init; }
    public required string Failure { get; init; }
    public required int FailureCount { get; init; }
    public int? HttpStatusCode { get; init; }
    public int? ResponseTimeMs { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
}

public sealed record IncidentAcknowledgedV1 : IncidentEventBase
{
    public override string EventType => "incident.incident.acknowledged";
    public override int Version => 1;

    public required Guid AcknowledgedBy { get; init; }
    public required DateTimeOffset AcknowledgedAt { get; init; }
}

public sealed record IncidentResolvedV1 : IncidentEventBase
{
    public override string EventType => "incident.incident.resolved";
    public override int Version => 1;

    /// <summary>Vrai si le service est revenu seul ; faux si quelqu'un a clos l'incident.</summary>
    public required bool Automatic { get; init; }
    public Guid? ResolvedBy { get; init; }
    public required DateTimeOffset ResolvedAt { get; init; }
    public required int DurationSeconds { get; init; }
}

/// <summary>Une analyse a été rattachée à l'incident. Sert au rafraîchissement de l'interface.</summary>
public sealed record IncidentAnalyzedV1 : IncidentEventBase
{
    public override string EventType => "incident.incident.analyzed";
    public override int Version => 1;

    public required string Provider { get; init; }
    public required string Summary { get; init; }
}
