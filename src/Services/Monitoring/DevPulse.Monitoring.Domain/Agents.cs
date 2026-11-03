using System.Security.Cryptography;
using System.Text;

namespace DevPulse.Monitoring.Domain;

public sealed class AgentException(string message) : Exception(message);

/// <summary>Ce qu'un agent envoie à chaque battement. Les valeurs hors bornes sont refusées, pas corrigées.</summary>
public sealed record AgentReport(
    string Hostname, string OperatingSystem, string AgentVersion, int IntervalSeconds,
    double CpuPercent, double MemoryPercent, double DiskPercent, long UptimeSeconds, DateTimeOffset ReceivedAt)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Hostname) || Hostname.Length > 255) throw new AgentException("Nom d'hôte invalide.");
        if (OperatingSystem.Length > 100 || AgentVersion.Length > 50) throw new AgentException("Description de l'agent trop longue.");
        if (IntervalSeconds is < 10 or > 3600) throw new AgentException("L'intervalle doit être compris entre 10 et 3600 s.");
        foreach (var value in new[] { CpuPercent, MemoryPercent, DiskPercent })
            if (double.IsNaN(value) || value is < 0 or > 100) throw new AgentException("Un pourcentage doit être compris entre 0 et 100.");
        if (UptimeSeconds < 0) throw new AgentException("Durée de fonctionnement invalide.");
    }
}

/// <summary>
/// Agent installé chez le client (monitoring hybride, ADR-005). Il s'authentifie par une clé propre, affichée une
/// seule fois : son organisation vient de la clé, jamais du contenu envoyé. Révoquer la clé coupe l'agent.
/// </summary>
public sealed class Agent
{
    public const string KeyPrefix = "dpa";

    /// <summary>Un agent est déclaré silencieux après trois battements manqués, et jamais avant une minute.</summary>
    public const int MissedBeatsBeforeSilent = 3;

    private Agent() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = "";
    public string Environment { get; private set; } = "";
    public string KeyHash { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public DateTimeOffset? LastSeenAt { get; private set; }
    public string? Hostname { get; private set; }
    public string? OperatingSystem { get; private set; }
    public string? AgentVersion { get; private set; }
    public int IntervalSeconds { get; private set; } = 30;
    public double? CpuPercent { get; private set; }
    public double? MemoryPercent { get; private set; }
    public double? DiskPercent { get; private set; }
    public long? UptimeSeconds { get; private set; }

    /// <summary>Renseigné tant que l'agent est annoncé silencieux (une panne est en cours).</summary>
    public DateTimeOffset? SilentSince { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public static (Agent Agent, string Key) Register(Guid organizationId, string name, string environment, DateTimeOffset now)
    {
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > 100) throw new AgentException("Le nom de l'agent fait de 1 à 100 caractères.");
        if (environment is not ("Production" or "Staging" or "Development")) throw new AgentException("Environnement invalide.");

        var agent = new Agent { Id = Guid.CreateVersion7(now), OrganizationId = organizationId, Name = name, Environment = environment, CreatedAt = now };
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        agent.KeyHash = Hash(secret);
        return (agent, $"{KeyPrefix}_{agent.Id:N}_{secret}");
    }

    /// <summary>Découpe une clé « dpa_{id}_{secret} » ; null si elle n'a pas cette forme.</summary>
    public static (Guid AgentId, string Secret)? ParseKey(string? key)
    {
        var parts = key?.Split('_');
        return parts is [KeyPrefix, var id, var secret] && Guid.TryParseExact(id, "N", out var agentId) && secret.Length == 48
            ? (agentId, secret)
            : null;
    }

    /// <summary>Comparaison en temps constant des empreintes : la réponse ne renseigne pas sur la clé attendue.</summary>
    public bool Accepts(string secret) =>
        !IsRevoked && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(secret)), Encoding.ASCII.GetBytes(KeyHash));

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    public HealthStatus Status => IsRevoked ? HealthStatus.Unknown : LastSeenAt is null ? HealthStatus.Unknown : SilentSince is null ? HealthStatus.Operational : HealthStatus.Down;

    /// <summary>Enregistre un battement. Renvoie le changement, pour publier une reprise si l'agent était silencieux.</summary>
    public TargetChange Report(AgentReport report)
    {
        report.Validate();
        var previous = Status;

        LastSeenAt = report.ReceivedAt;
        Hostname = report.Hostname.Trim();
        OperatingSystem = report.OperatingSystem;
        AgentVersion = report.AgentVersion;
        IntervalSeconds = report.IntervalSeconds;
        CpuPercent = Math.Round(report.CpuPercent, 1);
        MemoryPercent = Math.Round(report.MemoryPercent, 1);
        DiskPercent = Math.Round(report.DiskPercent, 1);
        UptimeSeconds = report.UptimeSeconds;

        var endedSince = SilentSince;
        SilentSince = null;
        return new TargetChange(previous, Status, OutageStarted: false, EndedOutageSince: endedSince);
    }

    public TimeSpan SilenceThreshold => TimeSpan.FromSeconds(Math.Max(60, IntervalSeconds * MissedBeatsBeforeSilent));

    /// <summary>Vrai si l'agent vient de dépasser son seuil de silence : la panne est annoncée une seule fois.</summary>
    public bool CheckSilence(DateTimeOffset now, out TargetChange change)
    {
        change = new TargetChange(Status, Status, false, null);
        if (IsRevoked || LastSeenAt is null || SilentSince is not null || now - LastSeenAt < SilenceThreshold) return false;

        var previous = Status;
        SilentSince = LastSeenAt;
        change = new TargetChange(previous, Status, OutageStarted: true, EndedOutageSince: null);
        return true;
    }

    public int MissedBeats(DateTimeOffset now) => LastSeenAt is null ? 0 : (int)((now - LastSeenAt.Value).TotalSeconds / IntervalSeconds);

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}

/// <summary>Un point d'historique des mesures d'un agent, purgé avec les vérifications.</summary>
public sealed class AgentSample
{
    private AgentSample() { }

    public Guid Id { get; private set; }
    public Guid AgentId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public double CpuPercent { get; private set; }
    public double MemoryPercent { get; private set; }
    public double DiskPercent { get; private set; }
    public DateTimeOffset At { get; private set; }

    public static AgentSample From(Agent agent, AgentReport report) => new()
    {
        Id = Guid.CreateVersion7(report.ReceivedAt),
        AgentId = agent.Id,
        OrganizationId = agent.OrganizationId,
        CpuPercent = Math.Round(report.CpuPercent, 1),
        MemoryPercent = Math.Round(report.MemoryPercent, 1),
        DiskPercent = Math.Round(report.DiskPercent, 1),
        At = report.ReceivedAt,
    };
}
