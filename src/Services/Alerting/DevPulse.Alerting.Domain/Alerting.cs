using System.Security.Cryptography;

namespace DevPulse.Alerting.Domain;

public sealed class DomainException(string message) : Exception(message);

public enum ChannelKind
{
    /// <summary>JSON signé (HMAC-SHA256) vers une URL de votre choix.</summary>
    Webhook,

    /// <summary>Message texte au format des webhooks entrants Slack (accepté aussi par Mattermost et Rocket.Chat).</summary>
    Slack,
}

public enum AlertEvent
{
    IncidentCreated,
    IncidentAcknowledged,
    IncidentResolved,
}

public enum DeliveryStatus
{
    Pending,
    Sent,
    Failed,
}

/// <summary>Une destination d'alerte, avec ses règles : quels événements, pour quels environnements.</summary>
public sealed class AlertChannel
{
    public const int MaxNameLength = 100;

    private AlertChannel() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = "";
    public ChannelKind Kind { get; private set; }
    public string Url { get; private set; } = "";

    /// <summary>Clé de signature des webhooks. Générée par DevPulse, montrée une seule fois à la création.</summary>
    public string SigningSecret { get; private set; } = "";

    public List<AlertEvent> Events { get; private set; } = [];

    /// <summary>Vide : tous les environnements.</summary>
    public List<string> Environments { get; private set; } = [];

    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static AlertChannel Create(
        Guid organizationId, string name, ChannelKind kind, string url, IEnumerable<AlertEvent> events, IEnumerable<string> environments, DateTimeOffset now)
    {
        var channel = new AlertChannel
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Kind = kind,
            SigningSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            CreatedAt = now,
        };
        channel.Update(name, url, events, environments, enabled: true, now);
        return channel;
    }

    public void Update(string name, string url, IEnumerable<AlertEvent> events, IEnumerable<string> environments, bool enabled, DateTimeOffset now)
    {
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength) throw new DomainException($"Le nom fait de 1 à {MaxNameLength} caractères.");

        var selected = events.Distinct().ToList();
        if (selected.Count == 0) throw new DomainException("Choisissez au moins un événement.");

        var scopes = environments.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct().ToList();
        if (scopes.Any(e => e is not ("Production" or "Staging" or "Development")))
            throw new DomainException("Environnements acceptés : Production, Staging, Development.");

        Name = name;
        Url = url.Trim();
        Events = selected;
        Environments = scopes;
        Enabled = enabled;
        UpdatedAt = now;
    }

    public bool Accepts(AlertEvent alertEvent, string environment) =>
        Enabled && Events.Contains(alertEvent) && (Environments.Count == 0 || Environments.Contains(environment));
}

/// <summary>
/// Un envoi à faire, écrit dans la transaction qui traite l'événement : un incident ne peut pas être « oublié »
/// entre sa réception et l'appel au webhook. Les échecs sont repris avec un délai croissant, puis abandonnés.
/// </summary>
public sealed class AlertDelivery
{
    public static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    private AlertDelivery() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid ChannelId { get; private set; }
    public Guid IncidentId { get; private set; }
    public AlertEvent Event { get; private set; }
    public string ServiceName { get; private set; } = "";
    public string Payload { get; private set; } = "";
    public DeliveryStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public string? LastError { get; private set; }
    public int? LastStatusCode { get; private set; }

    /// <summary>Moment du fait signalé (panne confirmée, prise en charge, résolution) : sert à mesurer le délai de notification.</summary>
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    public static AlertDelivery Create(
        AlertChannel channel, Guid incidentId, AlertEvent alertEvent, string serviceName, string payload, DateTimeOffset occurredAt, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        OrganizationId = channel.OrganizationId,
        ChannelId = channel.Id,
        IncidentId = incidentId,
        Event = alertEvent,
        ServiceName = serviceName,
        Payload = payload,
        Status = DeliveryStatus.Pending,
        NextAttemptAt = now,
        OccurredAt = occurredAt,
        CreatedAt = now,
    };

    public void MarkSent(int statusCode, DateTimeOffset now)
    {
        Attempts++;
        Status = DeliveryStatus.Sent;
        LastStatusCode = statusCode;
        LastError = null;
        SentAt = now;
    }

    public void MarkFailed(int? statusCode, string error, DateTimeOffset now)
    {
        Attempts++;
        LastStatusCode = statusCode;
        LastError = error.Length > 500 ? error[..500] : error;

        if (Attempts > RetryDelays.Length) Status = DeliveryStatus.Failed;
        else NextAttemptAt = now + RetryDelays[Attempts - 1];
    }

    public int? LatencyMilliseconds => SentAt is { } sent ? (int)(sent - OccurredAt).TotalMilliseconds : null;
}
