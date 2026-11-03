using DevPulse.Contracts.Events.Common;

namespace DevPulse.Contracts.Events.Monitoring;

public sealed record ServiceCheckCompletedV1 : IntegrationEvent
{
    public override string EventType => "monitoring.service.check-completed";
    public override int Version => 1;

    public required Guid ServiceId { get; init; }
    public required string Status { get; init; }
    public required int ResponseTimeMs { get; init; }
    public int? HttpStatusCode { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }
}

public sealed record ServiceDownDetectedV1 : IntegrationEvent
{
    public override string EventType => "monitoring.service.down-detected";
    public override int Version => 1;

    public required Guid ServiceId { get; init; }
    public required int FailureCount { get; init; }
    public int? LastHttpStatusCode { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
}

public sealed record ServiceRecoveredV1 : IntegrationEvent
{
    public override string EventType => "monitoring.service.recovered";
    public override int Version => 1;

    public required Guid ServiceId { get; init; }
    public required DateTimeOffset RecoveredAt { get; init; }
}
