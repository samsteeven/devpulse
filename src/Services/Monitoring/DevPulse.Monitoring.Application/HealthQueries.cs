using DevPulse.Monitoring.Domain;

namespace DevPulse.Monitoring.Application;

public sealed record ServiceHealthResponse(
    Guid ServiceId, string Name, string Environment, string Status, int ConsecutiveFailures,
    DateTimeOffset? LastCheckedAt, int? LastResponseTimeMs, int? LastHttpStatusCode, string? LastFailure);

public sealed record OverviewResponse(int Total, int Operational, int Degraded, int Down, int Unknown, IReadOnlyList<ServiceHealthResponse> Services);

public sealed record MetricsPoint(DateTimeOffset BucketStart, int Checks, int Failures, int AverageResponseTimeMs);

public sealed record ServiceMetricsResponse(
    Guid ServiceId, int Hours, int Checks, double? UptimePercent, int? AverageResponseTimeMs, int? P95ResponseTimeMs, IReadOnlyList<MetricsPoint> Series);

public sealed class HealthQueries(IHealthReadStore store, TimeProvider clock)
{
    public const int MaxHours = 168;

    public async Task<OverviewResponse> GetOverviewAsync(Guid organizationId, CancellationToken ct)
    {
        var services = (await store.ListTargetsAsync(organizationId, ct)).Select(ToResponse).ToList();
        int Count(HealthStatus status) => services.Count(s => s.Status == status.ToString());
        return new OverviewResponse(
            services.Count, Count(HealthStatus.Operational), Count(HealthStatus.Degraded),
            Count(HealthStatus.Down), Count(HealthStatus.Unknown), services);
    }

    public async Task<ServiceHealthResponse?> GetHealthAsync(Guid organizationId, Guid serviceId, CancellationToken ct) =>
        await store.FindTargetAsync(organizationId, serviceId, ct) is { } target ? ToResponse(target) : null;

    public async Task<ServiceMetricsResponse?> GetMetricsAsync(Guid organizationId, Guid serviceId, int hours, CancellationToken ct)
    {
        if (await store.FindTargetAsync(organizationId, serviceId, ct) is null) return null;

        hours = Math.Clamp(hours, 1, MaxHours);
        var now = clock.GetUtcNow();
        var checks = await store.ListChecksAsync(organizationId, serviceId, now.AddHours(-hours), ct);
        return Compute(serviceId, hours, checks);
    }

    /// <summary>La disponibilité compte un service dégradé comme disponible : il répond, mais lentement.</summary>
    public static ServiceMetricsResponse Compute(Guid serviceId, int hours, IReadOnlyList<CheckPoint> checks)
    {
        if (checks.Count == 0) return new ServiceMetricsResponse(serviceId, hours, 0, null, null, null, []);

        var answered = checks.Where(c => c.Status != HealthStatus.Down).Select(c => c.ResponseTimeMs).Order().ToList();
        var uptime = Math.Round(100d * answered.Count / checks.Count, 3);

        // Le temps d'une sonde en échec est souvent son délai d'attente : il fausserait la latence du service.
        int? average = answered.Count == 0 ? null : (int)answered.Average();
        int? p95 = answered.Count == 0 ? null : answered[(int)Math.Ceiling(answered.Count * 0.95) - 1];

        var bucket = TimeSpan.FromMinutes(hours <= 6 ? 1 : hours <= 48 ? 5 : 60);
        var series = checks
            .GroupBy(c => new DateTimeOffset(c.CheckedAt.Ticks - c.CheckedAt.Ticks % bucket.Ticks, TimeSpan.Zero))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var ok = g.Where(c => c.Status != HealthStatus.Down).ToList();
                return new MetricsPoint(g.Key, g.Count(), g.Count() - ok.Count, ok.Count == 0 ? 0 : (int)ok.Average(c => c.ResponseTimeMs));
            })
            .ToList();

        return new ServiceMetricsResponse(serviceId, hours, checks.Count, uptime, average, p95, series);
    }

    private static ServiceHealthResponse ToResponse(MonitoredTarget t) => new(
        t.ServiceId, t.Name, t.Environment, t.CurrentStatus.ToString(), t.ConsecutiveFailures,
        t.LastCheckedAt, t.LastResponseTimeMs, t.LastHttpStatusCode,
        t.LastFailure == ProbeFailure.None ? null : t.LastFailure.ToString());
}
