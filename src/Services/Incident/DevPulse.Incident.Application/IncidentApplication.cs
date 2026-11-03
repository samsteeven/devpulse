using DevPulse.Contracts.Events.AI;
using DevPulse.Contracts.Events.Incident;
using DevPulse.Contracts.Events.Monitoring;
using DevPulse.Contracts.Messaging;
using DevPulse.Incident.Domain;
using Microsoft.Extensions.Logging;
using IncidentEntity = DevPulse.Incident.Domain.Incident;

namespace DevPulse.Incident.Application;

public enum IncidentFilter { Active, Resolved, All }

/// <summary>Toutes les lectures prennent l'organisation en paramètre (ADR-011).</summary>
public interface IIncidentRepository
{
    Task<IReadOnlyList<IncidentEntity>> ListAsync(Guid organizationId, IncidentFilter filter, Guid? serviceId, int limit, CancellationToken ct);
    Task<IncidentEntity?> FindAsync(Guid organizationId, Guid id, CancellationToken ct);

    /// <summary>Lecture interne, déclenchée par un événement : l'organisation vient de l'événement, pas d'un utilisateur.</summary>
    Task<IncidentEntity?> FindActiveForServiceAsync(Guid organizationId, Guid serviceId, CancellationToken ct);
    Task<IncidentEntity?> FindByIdForEventAsync(Guid organizationId, Guid id, CancellationToken ct);

    Task<ServiceOutageState> GetOutageStateAsync(Guid organizationId, Guid serviceId, CancellationToken ct);
    Task<IncidentStatsResponse> StatsAsync(Guid organizationId, DateTimeOffset since, CancellationToken ct);
    void Add(IncidentEntity incident);
    Task SaveAsync(CancellationToken ct);
}

public sealed record TimelineEntryResponse(DateTimeOffset At, string Kind, string Message, string? Author);

public sealed record AnalysisResponse(string Provider, string? Model, string Summary, IReadOnlyList<string> ProbableCauses, IReadOnlyList<string> SuggestedActions, DateTimeOffset GeneratedAt);

public sealed record IncidentSummary(
    Guid Id, Guid ServiceId, string ServiceName, string Environment, string Title, string Severity, string Status,
    string Source, string Failure, DateTimeOffset DetectedAt, DateTimeOffset OpenedAt, DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? ResolvedAt, bool ResolvedAutomatically, int DurationSeconds, bool HasAnalysis);

public sealed record IncidentDetails(
    IncidentSummary Incident, int FailureCount, int? HttpStatusCode, int? ResponseTimeMs,
    AnalysisResponse? Analysis, IReadOnlyList<TimelineEntryResponse> Timeline);

public sealed record IncidentStatsResponse(int Open, int Acknowledged, int ResolvedInPeriod, int? MeanTimeToResolveSeconds, int PeriodDays);

public sealed record ResolveRequest(string? Note);
public sealed record NoteRequest(string Text);

public static class IncidentEvents
{
    public const string Producer = "incident-service";

    public static IncidentCreatedV1 Created(IncidentEntity i, string correlationId, string? causationId) => new()
    {
        Producer = Producer, OrganizationId = i.OrganizationId, CorrelationId = correlationId, CausationId = causationId,
        IncidentId = i.Id, ServiceId = i.ServiceId, ServiceName = i.ServiceName, Environment = i.Environment,
        Severity = i.Severity.ToString(), Title = i.Title, Source = i.Source, Failure = i.Failure, FailureCount = i.FailureCount,
        HttpStatusCode = i.HttpStatusCode, ResponseTimeMs = i.ResponseTimeMs, DetectedAt = i.DetectedAt, OpenedAt = i.OpenedAt,
    };

    public static IncidentAcknowledgedV1 Acknowledged(IncidentEntity i, string correlationId) => new()
    {
        Producer = Producer, OrganizationId = i.OrganizationId, CorrelationId = correlationId,
        IncidentId = i.Id, ServiceId = i.ServiceId, ServiceName = i.ServiceName, Environment = i.Environment,
        Severity = i.Severity.ToString(), Title = i.Title, AcknowledgedBy = i.AcknowledgedBy!.Value, AcknowledgedAt = i.AcknowledgedAt!.Value,
    };

    public static IncidentResolvedV1 Resolved(IncidentEntity i, string correlationId, string? causationId) => new()
    {
        Producer = Producer, OrganizationId = i.OrganizationId, CorrelationId = correlationId, CausationId = causationId,
        IncidentId = i.Id, ServiceId = i.ServiceId, ServiceName = i.ServiceName, Environment = i.Environment,
        Severity = i.Severity.ToString(), Title = i.Title, Automatic = i.ResolvedAutomatically, ResolvedBy = i.ResolvedBy,
        ResolvedAt = i.ResolvedAt!.Value, DurationSeconds = i.DurationSeconds(i.ResolvedAt!.Value),
    };

    public static IncidentAnalyzedV1 Analyzed(IncidentEntity i, string correlationId, string? causationId) => new()
    {
        Producer = Producer, OrganizationId = i.OrganizationId, CorrelationId = correlationId, CausationId = causationId,
        IncidentId = i.Id, ServiceId = i.ServiceId, ServiceName = i.ServiceName, Environment = i.Environment,
        Severity = i.Severity.ToString(), Title = i.Title, Provider = i.AnalysisProvider!, Summary = i.AnalysisSummary!,
    };
}

public sealed class IncidentService(IIncidentRepository repository, IOutbox outbox, TimeProvider clock)
{
    public const int MaxLimit = 200;

    public async Task<IReadOnlyList<IncidentSummary>> ListAsync(Guid organizationId, IncidentFilter filter, Guid? serviceId, int limit, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return (await repository.ListAsync(organizationId, filter, serviceId, Math.Clamp(limit, 1, MaxLimit), ct)).Select(i => ToSummary(i, now)).ToList();
    }

    public async Task<IncidentDetails?> GetAsync(Guid organizationId, Guid id, CancellationToken ct) =>
        await repository.FindAsync(organizationId, id, ct) is { } incident ? ToDetails(incident, clock.GetUtcNow()) : null;

    public Task<IncidentStatsResponse> StatsAsync(Guid organizationId, CancellationToken ct) =>
        repository.StatsAsync(organizationId, clock.GetUtcNow().AddDays(-30), ct);

    public async Task<IncidentDetails?> AcknowledgeAsync(Guid organizationId, Guid id, Actor actor, CancellationToken ct)
    {
        var incident = await repository.FindAsync(organizationId, id, ct);
        if (incident is null) return null;

        if (incident.Acknowledge(actor, clock.GetUtcNow()))
            outbox.Enqueue(IncidentEvents.Acknowledged(incident, Guid.NewGuid().ToString("N")));
        await repository.SaveAsync(ct);
        return ToDetails(incident, clock.GetUtcNow());
    }

    public async Task<IncidentDetails?> ResolveAsync(Guid organizationId, Guid id, Actor actor, string? note, CancellationToken ct)
    {
        var incident = await repository.FindAsync(organizationId, id, ct);
        if (incident is null) return null;

        var resolved = incident.Resolve(actor, note, clock.GetUtcNow());
        if (resolved) outbox.Enqueue(IncidentEvents.Resolved(incident, Guid.NewGuid().ToString("N"), null));
        await repository.SaveAsync(ct);
        if (resolved) IncidentMetrics.RecordResolved(incident);
        return ToDetails(incident, clock.GetUtcNow());
    }

    public async Task<IncidentDetails?> AddNoteAsync(Guid organizationId, Guid id, Actor actor, string text, CancellationToken ct)
    {
        var incident = await repository.FindAsync(organizationId, id, ct);
        if (incident is null) return null;

        incident.AddNote(actor, text, clock.GetUtcNow());
        await repository.SaveAsync(ct);
        return ToDetails(incident, clock.GetUtcNow());
    }

    public static IncidentSummary ToSummary(IncidentEntity i, DateTimeOffset now) => new(
        i.Id, i.ServiceId, i.ServiceName, i.Environment, i.Title, i.Severity.ToString(), i.Status.ToString(), i.Source, i.Failure,
        i.DetectedAt, i.OpenedAt, i.AcknowledgedAt, i.ResolvedAt, i.ResolvedAutomatically, i.DurationSeconds(now), i.AnalyzedAt is not null);

    public static IncidentDetails ToDetails(IncidentEntity i, DateTimeOffset now) => new(
        ToSummary(i, now), i.FailureCount, i.HttpStatusCode, i.ResponseTimeMs,
        i.AnalyzedAt is null ? null : new AnalysisResponse(i.AnalysisProvider!, i.AnalysisModel, i.AnalysisSummary!, i.ProbableCauses, i.SuggestedActions, i.AnalyzedAt.Value),
        i.Timeline.OrderBy(t => t.At).Select(t => new TimelineEntryResponse(t.At, t.Kind.ToString(), t.Message, t.AuthorEmail)).ToList());
}

/// <summary>Panne confirmée : ouvre un incident, sauf s'il y en a déjà un en cours pour ce service ou si la panne est déjà terminée.</summary>
public sealed class ServiceDownDetectedHandler(IIncidentRepository repository, IOutbox outbox, TimeProvider clock, ILogger<ServiceDownDetectedHandler> logger)
    : IIntegrationEventHandler<ServiceDownDetectedV1>
{
    public async Task HandleAsync(ServiceDownDetectedV1 e, CancellationToken ct)
    {
        var state = await repository.GetOutageStateAsync(e.OrganizationId, e.ServiceId, ct);
        if (!state.RecordDetection(e.DetectedAt))
        {
            logger.LogInformation("Panne de {ServiceId} déjà terminée d'après une reprise reçue avant elle : pas d'incident", e.ServiceId);
            return;
        }

        if (await repository.FindActiveForServiceAsync(e.OrganizationId, e.ServiceId, ct) is not null) return;

        var incident = IncidentEntity.Open(new Detection(
            e.OrganizationId, e.ServiceId, e.ServiceName, e.Environment, e.Source, e.Failure, e.FailureCount,
            e.LastHttpStatusCode, e.LastResponseTimeMs, e.DetectedAt), clock.GetUtcNow());
        repository.Add(incident);
        outbox.Enqueue(IncidentEvents.Created(incident, e.CorrelationId, e.EventId.ToString()));
        // Compté avant la validation, faite par le consommateur : un échec suivi d'une reprise peut compter deux fois.
        IncidentMetrics.RecordOpened(incident);
        logger.LogInformation("Incident {IncidentId} ouvert pour {ServiceName}", incident.Id, e.ServiceName);
    }
}

public sealed class ServiceRecoveredHandler(IIncidentRepository repository, IOutbox outbox, ILogger<ServiceRecoveredHandler> logger)
    : IIntegrationEventHandler<ServiceRecoveredV1>
{
    public async Task HandleAsync(ServiceRecoveredV1 e, CancellationToken ct)
    {
        var state = await repository.GetOutageStateAsync(e.OrganizationId, e.ServiceId, ct);
        state.RecordRecovery(e.RecoveredAt);

        var incident = await repository.FindActiveForServiceAsync(e.OrganizationId, e.ServiceId, ct);
        if (incident is null || !incident.ResolveAutomatically(e.RecoveredAt)) return;

        outbox.Enqueue(IncidentEvents.Resolved(incident, e.CorrelationId, e.EventId.ToString()));
        IncidentMetrics.RecordResolved(incident);
        logger.LogInformation("Incident {IncidentId} clos automatiquement", incident.Id);
    }
}

public sealed class AnalysisCompletedHandler(IIncidentRepository repository, IOutbox outbox) : IIntegrationEventHandler<AnalysisCompletedV1>
{
    public async Task HandleAsync(AnalysisCompletedV1 e, CancellationToken ct)
    {
        var incident = await repository.FindByIdForEventAsync(e.OrganizationId, e.IncidentId, ct);
        if (incident is null) return;

        var attached = incident.AttachAnalysis(new AnalysisProposal(e.Provider, e.Model, e.Summary, e.ProbableCauses, e.SuggestedActions, e.GeneratedAt));
        if (attached) outbox.Enqueue(IncidentEvents.Analyzed(incident, e.CorrelationId, e.EventId.ToString()));
    }
}
