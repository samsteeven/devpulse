using DevPulse.Contracts.Internal;
using DevPulse.Monitoring.Domain;

namespace DevPulse.Monitoring.Application;

public interface IRegistryClient
{
    Task<IReadOnlyList<ActiveServiceDto>> GetActiveServicesAsync(CancellationToken ct);
}

public interface ITargetProber
{
    Task<ProbeResult> ProbeAsync(string targetUrl, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Ce dont le worker a besoin pour une vérification, sans dépendre d'une entité suivie par le contexte EF.</summary>
public sealed record CheckAssignment(Guid ServiceId, string TargetUrl, TimeSpan Timeout);

public interface ITargetStore
{
    Task SynchronizeAsync(IReadOnlyList<ActiveServiceDto> active, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Réserve au plus <paramref name="max"/> cibles arrivées à échéance et repousse leur prochaine échéance d'un intervalle.
    /// La réservation est atomique : deux workers ne peuvent pas prendre la même cible, et un worker tombé ne bloque
    /// rien (la cible revient à l'échéance suivante).
    /// </summary>
    Task<IReadOnlyList<CheckAssignment>> ClaimDueAsync(int max, DateTimeOffset now, CancellationToken ct);

    Task RecordAsync(Guid serviceId, ProbeResult result, CancellationToken ct);

    Task<int> PurgeOlderThanAsync(DateTimeOffset limit, CancellationToken ct);
}

public interface IHealthReadStore
{
    Task<IReadOnlyList<MonitoredTarget>> ListTargetsAsync(Guid organizationId, CancellationToken ct);
    Task<MonitoredTarget?> FindTargetAsync(Guid organizationId, Guid serviceId, CancellationToken ct);
    Task<IReadOnlyList<CheckPoint>> ListChecksAsync(Guid organizationId, Guid serviceId, DateTimeOffset since, CancellationToken ct);
}

public sealed record CheckPoint(HealthStatus Status, int ResponseTimeMs, DateTimeOffset CheckedAt);
