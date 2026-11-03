using System.ComponentModel.DataAnnotations;
using DevPulse.Monitoring.Domain;

namespace DevPulse.Monitoring.Application;

public sealed record AgentRegistrationRequest([Required] string Name, [Required] string Environment);

public sealed record AgentReportRequest(
    [Required] string Hostname, string OperatingSystem, string AgentVersion, int IntervalSeconds,
    double CpuPercent, double MemoryPercent, double DiskPercent, long UptimeSeconds);

public sealed record AgentResponse(
    Guid Id, string Name, string Environment, string Status, DateTimeOffset? LastSeenAt, string? Hostname, string? OperatingSystem,
    string? AgentVersion, int IntervalSeconds, double? CpuPercent, double? MemoryPercent, double? DiskPercent, long? UptimeSeconds,
    DateTimeOffset CreatedAt, bool Revoked)
{
    public static AgentResponse From(Agent a) => new(
        a.Id, a.Name, a.Environment, a.Status.ToString(), a.LastSeenAt, a.Hostname, a.OperatingSystem, a.AgentVersion, a.IntervalSeconds,
        a.CpuPercent, a.MemoryPercent, a.DiskPercent, a.UptimeSeconds, a.CreatedAt, a.IsRevoked);
}

/// <summary>La clé n'est renvoyée qu'à la création : seule son empreinte est conservée.</summary>
public sealed record AgentCreated(AgentResponse Agent, string Key);

public sealed record AgentSamplePoint(DateTimeOffset At, double CpuPercent, double MemoryPercent, double DiskPercent);

public enum IngestOutcome { Accepted, Unauthorized }

public interface IAgentStore
{
    Task<AgentCreated> RegisterAsync(Guid organizationId, AgentRegistrationRequest request, CancellationToken ct);
    Task<IReadOnlyList<AgentResponse>> ListAsync(Guid organizationId, CancellationToken ct);
    Task<bool> RevokeAsync(Guid organizationId, Guid agentId, CancellationToken ct);
    Task<IReadOnlyList<AgentSamplePoint>?> SamplesAsync(Guid organizationId, Guid agentId, DateTimeOffset since, CancellationToken ct);

    /// <summary>Clé invalide, inconnue ou révoquée : même réponse, pour ne rien révéler.</summary>
    Task<IngestOutcome> IngestAsync(string? key, AgentReportRequest report, CancellationToken ct);

    /// <summary>Annonce les agents devenus silencieux. Renvoie leur nombre.</summary>
    Task<int> DetectSilentAsync(DateTimeOffset now, CancellationToken ct);
}
