namespace DevPulse.Incident.Domain;

public class DomainException(string message) : Exception(message);

/// <summary>L'action n'est plus possible dans l'état actuel de l'incident (conflit, et non donnée invalide).</summary>
public sealed class InvalidTransitionException(string message) : DomainException(message);

public enum IncidentStatus
{
    Open,
    Acknowledged,
    Resolved,
}

public enum IncidentSeverity
{
    Critical,
    Major,
    Minor,
}

public enum TimelineKind
{
    Detected,
    Acknowledged,
    Note,
    Analysis,
    Recovered,
    Resolved,
}

public sealed class TimelineEntry
{
    private TimelineEntry() { }

    public Guid Id { get; private set; }
    public Guid IncidentId { get; private set; }
    public TimelineKind Kind { get; private set; }
    public DateTimeOffset At { get; private set; }
    public Guid? AuthorId { get; private set; }
    public string? AuthorEmail { get; private set; }
    public string Message { get; private set; } = "";

    internal static TimelineEntry Create(Guid incidentId, TimelineKind kind, DateTimeOffset at, string message, Actor? author = null) => new()
    {
        Id = Guid.CreateVersion7(at),
        IncidentId = incidentId,
        Kind = kind,
        At = at,
        AuthorId = author?.UserId,
        AuthorEmail = author?.Email,
        Message = message,
    };
}

/// <summary>Qui agit : l'identifiant vient du jeton, l'adresse sert seulement à l'affichage de la chronologie.</summary>
public sealed record Actor(Guid UserId, string? Email);

/// <summary>Ce que Monitoring a constaté au moment de la panne confirmée.</summary>
public sealed record Detection(
    Guid OrganizationId, Guid ServiceId, string ServiceName, string Environment, string Source,
    string Failure, int FailureCount, int? HttpStatusCode, int? ResponseTimeMs, DateTimeOffset DetectedAt);

public sealed record AnalysisProposal(
    string Provider, string? Model, string Summary, IReadOnlyList<string> ProbableCauses, IReadOnlyList<string> SuggestedActions, DateTimeOffset GeneratedAt);

/// <summary>
/// Un incident par panne. Il est ouvert par Incident lui-même à la réception d'une panne confirmée, et non par
/// Alerting : l'historique des pannes existe même si aucune alerte n'est configurée ou si une règle est coupée.
/// </summary>
public sealed class Incident
{
    private readonly List<TimelineEntry> _timeline = [];

    private Incident() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid ServiceId { get; private set; }
    public string ServiceName { get; private set; } = "";
    public string Environment { get; private set; } = "";
    public string Title { get; private set; } = "";
    public IncidentSeverity Severity { get; private set; }
    public IncidentStatus Status { get; private set; }
    public string Source { get; private set; } = "";
    public string Failure { get; private set; } = "";
    public int FailureCount { get; private set; }
    public int? HttpStatusCode { get; private set; }
    public int? ResponseTimeMs { get; private set; }

    public DateTimeOffset DetectedAt { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public Guid? AcknowledgedBy { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public Guid? ResolvedBy { get; private set; }
    public bool ResolvedAutomatically { get; private set; }

    public string? AnalysisProvider { get; private set; }
    public string? AnalysisModel { get; private set; }
    public string? AnalysisSummary { get; private set; }
    public List<string> ProbableCauses { get; private set; } = [];
    public List<string> SuggestedActions { get; private set; } = [];
    public DateTimeOffset? AnalyzedAt { get; private set; }

    public IReadOnlyList<TimelineEntry> Timeline => _timeline;

    public bool IsActive => Status != IncidentStatus.Resolved;

    public static IncidentSeverity SeverityFor(string environment) => environment switch
    {
        "Production" => IncidentSeverity.Critical,
        "Staging" => IncidentSeverity.Major,
        _ => IncidentSeverity.Minor,
    };

    public static Incident Open(Detection detection, DateTimeOffset now)
    {
        var incident = new Incident
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = detection.OrganizationId,
            ServiceId = detection.ServiceId,
            ServiceName = detection.ServiceName,
            Environment = detection.Environment,
            Severity = SeverityFor(detection.Environment),
            Status = IncidentStatus.Open,
            Title = detection.Source == "Agent" ? $"{detection.ServiceName} : l'agent ne donne plus de nouvelles" : $"{detection.ServiceName} ne répond plus",
            Source = detection.Source,
            Failure = detection.Failure,
            FailureCount = detection.FailureCount,
            HttpStatusCode = detection.HttpStatusCode,
            ResponseTimeMs = detection.ResponseTimeMs,
            DetectedAt = detection.DetectedAt,
            OpenedAt = now,
        };

        var detail = detection.HttpStatusCode is { } code ? $"{detection.Failure}, HTTP {code}" : detection.Failure;
        incident._timeline.Add(TimelineEntry.Create(incident.Id, TimelineKind.Detected, detection.DetectedAt,
            $"Panne confirmée après {detection.FailureCount} échec(s) consécutif(s) ({detail})."));
        return incident;
    }

    /// <summary>Renvoie faux si l'incident était déjà pris en charge : rejouer la commande ne change rien.</summary>
    public bool Acknowledge(Actor actor, DateTimeOffset now)
    {
        if (Status == IncidentStatus.Resolved) throw new InvalidTransitionException("Cet incident est déjà résolu.");
        if (Status == IncidentStatus.Acknowledged) return false;

        Status = IncidentStatus.Acknowledged;
        AcknowledgedAt = now;
        AcknowledgedBy = actor.UserId;
        _timeline.Add(TimelineEntry.Create(Id, TimelineKind.Acknowledged, now, "Pris en charge.", actor));
        return true;
    }

    public bool Resolve(Actor actor, string? note, DateTimeOffset now)
    {
        if (Status == IncidentStatus.Resolved) return false;

        note = note?.Trim();
        if (note is { Length: > 2000 }) throw new DomainException("La note de résolution dépasse 2000 caractères.");

        Status = IncidentStatus.Resolved;
        ResolvedAt = now;
        ResolvedBy = actor.UserId;
        _timeline.Add(TimelineEntry.Create(Id, TimelineKind.Resolved, now,
            string.IsNullOrEmpty(note) ? "Résolu manuellement." : $"Résolu manuellement : {note}", actor));
        return true;
    }

    /// <summary>Le service répond de nouveau. Une reprise antérieure à la détection (message arrivé en retard) ne ferme rien.</summary>
    public bool ResolveAutomatically(DateTimeOffset recoveredAt)
    {
        if (Status == IncidentStatus.Resolved || recoveredAt < DetectedAt) return false;

        Status = IncidentStatus.Resolved;
        ResolvedAt = recoveredAt;
        ResolvedAutomatically = true;
        _timeline.Add(TimelineEntry.Create(Id, TimelineKind.Recovered, recoveredAt, "Le service répond de nouveau : incident clos automatiquement."));
        return true;
    }

    public void AddNote(Actor actor, string text, DateTimeOffset now)
    {
        text = text?.Trim() ?? "";
        if (text.Length is 0 or > 2000) throw new DomainException("Une note fait de 1 à 2000 caractères.");
        _timeline.Add(TimelineEntry.Create(Id, TimelineKind.Note, now, text, actor));
    }

    /// <summary>Une seule analyse est conservée : une seconde livraison du même résultat ne la remplace pas.</summary>
    public bool AttachAnalysis(AnalysisProposal analysis)
    {
        if (AnalyzedAt is not null) return false;

        AnalysisProvider = analysis.Provider;
        AnalysisModel = analysis.Model;
        AnalysisSummary = Truncate(analysis.Summary, 2000);
        ProbableCauses = analysis.ProbableCauses.Take(5).Select(c => Truncate(c, 500)).ToList();
        SuggestedActions = analysis.SuggestedActions.Take(5).Select(a => Truncate(a, 500)).ToList();
        AnalyzedAt = analysis.GeneratedAt;
        _timeline.Add(TimelineEntry.Create(Id, TimelineKind.Analysis, analysis.GeneratedAt,
            analysis.Provider == "Llm" ? "Analyse proposée par le modèle de langage." : "Analyse proposée par les règles locales."));
        return true;
    }

    public int DurationSeconds(DateTimeOffset now) => (int)((ResolvedAt ?? now) - DetectedAt).TotalSeconds;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// Dernières dates connues par service. RabbitMQ ne garantit pas l'ordre : une reprise peut arriver avant la panne
/// qu'elle termine. Une panne plus ancienne que la dernière reprise connue est ignorée.
/// </summary>
public sealed class ServiceOutageState
{
    private ServiceOutageState() { }

    public Guid ServiceId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public DateTimeOffset? LastDetectedAt { get; private set; }
    public DateTimeOffset? LastRecoveredAt { get; private set; }

    public static ServiceOutageState For(Guid serviceId, Guid organizationId) => new() { ServiceId = serviceId, OrganizationId = organizationId };

    /// <summary>Faux si la panne est déjà terminée d'après une reprise reçue avant elle.</summary>
    public bool RecordDetection(DateTimeOffset detectedAt)
    {
        if (LastRecoveredAt is { } recovered && recovered >= detectedAt) return false;
        if (LastDetectedAt is null || detectedAt > LastDetectedAt) LastDetectedAt = detectedAt;
        return true;
    }

    public void RecordRecovery(DateTimeOffset recoveredAt)
    {
        if (LastRecoveredAt is null || recoveredAt > LastRecoveredAt) LastRecoveredAt = recoveredAt;
    }
}
