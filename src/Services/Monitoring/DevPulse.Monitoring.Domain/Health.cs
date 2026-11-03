namespace DevPulse.Monitoring.Domain;

public enum HealthStatus
{
    Unknown,
    Operational,
    Degraded,
    Down,
}

public enum ProbeFailure
{
    None,
    BlockedTarget,
    Timeout,
    ConnectionError,
    TlsError,
    Redirect,
    UnexpectedStatus,
}

public sealed record ProbeResult(HealthStatus Status, int? HttpStatusCode, int ResponseTimeMs, ProbeFailure Failure, DateTimeOffset CheckedAt);

public static class HealthClassifier
{
    /// <summary>Au-delà de cette durée, un service qui répond correctement est considéré comme dégradé.</summary>
    public static readonly TimeSpan DegradedThreshold = TimeSpan.FromSeconds(1);

    public static ProbeResult FromResponse(int httpStatusCode, TimeSpan elapsed, DateTimeOffset checkedAt)
    {
        var (status, failure) = httpStatusCode switch
        {
            >= 200 and < 300 when elapsed >= DegradedThreshold => (HealthStatus.Degraded, ProbeFailure.None),
            >= 200 and < 300 => (HealthStatus.Operational, ProbeFailure.None),
            // Une redirection n'est jamais suivie (elle pourrait viser une ressource interne) : le point de contrôle est mal configuré.
            >= 300 and < 400 => (HealthStatus.Down, ProbeFailure.Redirect),
            _ => (HealthStatus.Down, ProbeFailure.UnexpectedStatus),
        };
        return new ProbeResult(status, httpStatusCode, ToMilliseconds(elapsed), failure, checkedAt);
    }

    public static ProbeResult FromFailure(ProbeFailure failure, TimeSpan elapsed, DateTimeOffset checkedAt) =>
        new(HealthStatus.Down, null, ToMilliseconds(elapsed), failure, checkedAt);

    private static int ToMilliseconds(TimeSpan elapsed) => (int)Math.Min(elapsed.TotalMilliseconds, int.MaxValue);
}

/// <summary>Une vérification passée. Les données sont purgées au-delà de la durée de conservation.</summary>
public sealed class HealthCheck
{
    private HealthCheck() { }

    public Guid Id { get; private set; }
    public Guid ServiceId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public HealthStatus Status { get; private set; }
    public int ResponseTimeMs { get; private set; }
    public int? HttpStatusCode { get; private set; }
    public ProbeFailure Failure { get; private set; }
    public DateTimeOffset CheckedAt { get; private set; }

    public static HealthCheck From(Guid serviceId, Guid organizationId, ProbeResult result) => new()
    {
        Id = Guid.CreateVersion7(result.CheckedAt),
        ServiceId = serviceId,
        OrganizationId = organizationId,
        Status = result.Status,
        ResponseTimeMs = result.ResponseTimeMs,
        HttpStatusCode = result.HttpStatusCode,
        Failure = result.Failure,
        CheckedAt = result.CheckedAt,
    };
}

/// <summary>
/// Copie locale d'un service de Registry, avec son état courant. Monitoring n'interroge jamais la base de Registry :
/// il garde ce dont il a besoin, ce qui lui permet de continuer à surveiller si Registry est indisponible.
/// </summary>
public sealed class MonitoredTarget
{
    private MonitoredTarget() { }

    public Guid ServiceId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = "";
    public string Environment { get; private set; } = "";
    public string TargetUrl { get; private set; } = "";
    public int IntervalSeconds { get; private set; }
    public int TimeoutSeconds { get; private set; }
    public bool Enabled { get; private set; }
    public DateTimeOffset NextCheckAt { get; private set; }
    public HealthStatus CurrentStatus { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public DateTimeOffset? LastCheckedAt { get; private set; }
    public int? LastResponseTimeMs { get; private set; }
    public int? LastHttpStatusCode { get; private set; }
    public ProbeFailure LastFailure { get; private set; }

    public static MonitoredTarget New(
        Guid serviceId, Guid organizationId, string name, string environment, string targetUrl,
        int intervalSeconds, int timeoutSeconds, DateTimeOffset now) => new()
    {
        ServiceId = serviceId,
        OrganizationId = organizationId,
        Name = name,
        Environment = environment,
        TargetUrl = targetUrl,
        IntervalSeconds = intervalSeconds,
        TimeoutSeconds = timeoutSeconds,
        Enabled = true,
        NextCheckAt = now, // première vérification sans attendre l'intervalle
        CurrentStatus = HealthStatus.Unknown,
    };

    public void Reconfigure(string name, string environment, string targetUrl, int intervalSeconds, int timeoutSeconds, DateTimeOffset now)
    {
        // Une nouvelle URL est une nouvelle cible : son état passé ne dit plus rien.
        if (targetUrl != TargetUrl)
        {
            CurrentStatus = HealthStatus.Unknown;
            ConsecutiveFailures = 0;
            NextCheckAt = now;
        }
        else if (!Enabled)
        {
            NextCheckAt = now;
        }

        Name = name;
        Environment = environment;
        TargetUrl = targetUrl;
        IntervalSeconds = intervalSeconds;
        TimeoutSeconds = timeoutSeconds;
        Enabled = true;
    }

    public void Disable() => Enabled = false;

    public void Apply(ProbeResult result)
    {
        CurrentStatus = result.Status;
        ConsecutiveFailures = result.Status == HealthStatus.Down ? ConsecutiveFailures + 1 : 0;
        LastCheckedAt = result.CheckedAt;
        LastResponseTimeMs = result.ResponseTimeMs;
        LastHttpStatusCode = result.HttpStatusCode;
        LastFailure = result.Failure;
    }
}
