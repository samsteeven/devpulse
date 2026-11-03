namespace DevPulse.Contracts.Events.Common;

/// <summary>Enveloppe commune des événements : un contrat de transport, sans logique métier.</summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    /// <summary>Type métier, par exemple <c>monitoring.service.down-detected</c>.</summary>
    public abstract string EventType { get; }

    public abstract int Version { get; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public required string Producer { get; init; }

    public required Guid OrganizationId { get; init; }

    public required string CorrelationId { get; init; }

    public string? CausationId { get; init; }

    /// <summary>Clé de routage AMQP : <c>{domain}.{entity}.{event}.v{version}</c>.</summary>
    public string RoutingKey => $"{EventType}.v{Version}";
}
