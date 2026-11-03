using DevPulse.Contracts.Events.Common;

namespace DevPulse.Contracts.Events.Monitoring;

/// <summary>D'où vient la mesure : une sonde HTTP de DevPulse, ou un agent installé chez le client.</summary>
public static class MonitoringSources
{
    public const string Probe = "Probe";
    public const string Agent = "Agent";
}

/// <summary>
/// L'état affiché d'un service a changé (Opérationnel, Dégradé, En panne). Émis à chaque changement, pas à chaque
/// vérification : il sert au temps réel du tableau de bord, pas à ouvrir des incidents.
/// </summary>
public sealed record ServiceStatusChangedV1 : IntegrationEvent
{
    public override string EventType => "monitoring.service.status-changed";
    public override int Version => 1;

    public required Guid ServiceId { get; init; }
    public required string ServiceName { get; init; }
    public required string Environment { get; init; }
    public required string PreviousStatus { get; init; }
    public required string CurrentStatus { get; init; }
    public int? ResponseTimeMs { get; init; }
    public int? HttpStatusCode { get; init; }
    public string? Failure { get; init; }
    public required DateTimeOffset ChangedAt { get; init; }
}

/// <summary>Panne confirmée : plusieurs échecs consécutifs, ou un agent silencieux. C'est ce qui ouvre un incident.</summary>
public sealed record ServiceDownDetectedV1 : IntegrationEvent
{
    public override string EventType => "monitoring.service.down-detected";
    public override int Version => 1;

    public required Guid ServiceId { get; init; }
    public required string ServiceName { get; init; }
    public required string Environment { get; init; }
    public required string Source { get; init; }
    public required int FailureCount { get; init; }
    public required string Failure { get; init; }
    public int? LastHttpStatusCode { get; init; }
    public int? LastResponseTimeMs { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
}

/// <summary>Un service annoncé en panne répond de nouveau. Jamais émis sans <see cref="ServiceDownDetectedV1"/> préalable.</summary>
public sealed record ServiceRecoveredV1 : IntegrationEvent
{
    public override string EventType => "monitoring.service.recovered";
    public override int Version => 1;

    public required Guid ServiceId { get; init; }
    public required string ServiceName { get; init; }
    public required string Environment { get; init; }
    public required DateTimeOffset DownSince { get; init; }
    public required DateTimeOffset RecoveredAt { get; init; }
}
