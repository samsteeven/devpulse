using DevPulse.Contracts.Events.Monitoring;
using DevPulse.Contracts.Internal;
using DevPulse.Contracts.Messaging;
using DevPulse.Messaging;
using DevPulse.Monitoring.Application;
using DevPulse.Monitoring.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DevPulse.Monitoring.Infrastructure;

public sealed class MonitoringDb(DbContextOptions<MonitoringDb> options) : DbContext(options)
{
    public DbSet<MonitoredTarget> Targets => Set<MonitoredTarget>();
    public DbSet<HealthCheck> Checks => Set<HealthCheck>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.AddMessagingTables();

        model.Entity<MonitoredTarget>(e =>
        {
            e.ToTable("monitored_targets");
            e.HasKey(t => t.ServiceId);
            e.Property(t => t.ServiceId).ValueGeneratedNever();
            e.Property(t => t.Name).HasMaxLength(100);
            e.Property(t => t.Environment).HasMaxLength(20);
            e.Property(t => t.TargetUrl).HasMaxLength(1000);
            e.Property(t => t.CurrentStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.LastFailure).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(t => t.OrganizationId);
            e.HasIndex(t => t.NextCheckAt).HasFilter("enabled");
        });

        model.Entity<HealthCheck>(e =>
        {
            e.ToTable("health_checks");
            e.Property(c => c.Id).ValueGeneratedNever();
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.Failure).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(c => new { c.OrganizationId, c.ServiceId, c.CheckedAt });
            e.HasIndex(c => c.CheckedAt);
        });
    }
}

public sealed class MonitoringDbDesignTimeFactory : IDesignTimeDbContextFactory<MonitoringDb>
{
    public MonitoringDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<MonitoringDb>()
        .UseNpgsql("Host=design-time").UseSnakeCaseNamingConvention().Options);
}

public sealed class TargetStore(MonitoringDb db, IOutbox outbox) : ITargetStore
{
    public async Task SynchronizeAsync(IReadOnlyList<ActiveServiceDto> active, DateTimeOffset now, CancellationToken ct)
    {
        var known = await db.Targets.ToDictionaryAsync(t => t.ServiceId, ct);

        foreach (var service in active)
        {
            if (known.Remove(service.ServiceId, out var target))
                target.Reconfigure(service.Name, service.Environment, service.TargetUrl, service.CheckIntervalSeconds, service.TimeoutSeconds, now);
            else
                db.Targets.Add(MonitoredTarget.New(
                    service.ServiceId, service.OrganizationId, service.Name, service.Environment,
                    service.TargetUrl, service.CheckIntervalSeconds, service.TimeoutSeconds, now));
        }

        // Ce qui reste n'est plus actif dans Registry. L'historique est conservé jusqu'à la purge.
        foreach (var removed in known.Values) removed.Disable();

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CheckAssignment>> ClaimDueAsync(int max, DateTimeOffset now, CancellationToken ct)
    {
        // SKIP LOCKED : un autre worker en cours de réservation ne bloque pas celui-ci et ne reçoit pas les mêmes lignes.
        var claimed = await db.Targets.FromSqlInterpolated($"""
            UPDATE monitored_targets
            SET next_check_at = {now} + make_interval(secs => interval_seconds)
            WHERE service_id IN (
                SELECT service_id FROM monitored_targets
                WHERE enabled AND next_check_at <= {now}
                ORDER BY next_check_at
                LIMIT {max}
                FOR UPDATE SKIP LOCKED)
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);

        return claimed.Select(t => new CheckAssignment(t.ServiceId, t.TargetUrl, TimeSpan.FromSeconds(t.TimeoutSeconds))).ToList();
    }

    public async Task RecordAsync(Guid serviceId, ProbeResult result, CancellationToken ct)
    {
        var target = await db.Targets.FirstOrDefaultAsync(t => t.ServiceId == serviceId, ct);
        if (target is null) return;

        var change = target.Apply(result);
        db.Checks.Add(HealthCheck.From(serviceId, target.OrganizationId, result));
        MonitoringEvents.Publish(outbox, target, change, result);

        // SaveChanges écrit l'état, l'historique et les événements dans une seule transaction.
        await db.SaveChangesAsync(ct);
    }

    public Task<int> PurgeOlderThanAsync(DateTimeOffset limit, CancellationToken ct) =>
        db.Checks.Where(c => c.CheckedAt < limit).ExecuteDeleteAsync(ct);
}

public sealed class HealthReadStore(MonitoringDb db) : IHealthReadStore
{
    public async Task<IReadOnlyList<MonitoredTarget>> ListTargetsAsync(Guid organizationId, CancellationToken ct) =>
        await db.Targets.AsNoTracking()
            .Where(t => t.OrganizationId == organizationId && t.Enabled)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

    public Task<MonitoredTarget?> FindTargetAsync(Guid organizationId, Guid serviceId, CancellationToken ct) =>
        db.Targets.AsNoTracking().FirstOrDefaultAsync(t => t.OrganizationId == organizationId && t.ServiceId == serviceId, ct);

    public async Task<IReadOnlyList<CheckPoint>> ListChecksAsync(Guid organizationId, Guid serviceId, DateTimeOffset since, CancellationToken ct) =>
        await db.Checks.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.ServiceId == serviceId && c.CheckedAt >= since)
            .OrderBy(c => c.CheckedAt)
            .Select(c => new CheckPoint(c.Status, c.ResponseTimeMs, c.CheckedAt))
            .ToListAsync(ct);
}

/// <summary>Traduit un changement d'état en événements. Un seul identifiant de corrélation pour tout ce qu'une vérification déclenche.</summary>
public static class MonitoringEvents
{
    public const string Producer = "monitoring-service";

    public static void Publish(IOutbox outbox, MonitoredTarget target, TargetChange change, ProbeResult result, string source = MonitoringSources.Probe)
    {
        if (!change.StatusChanged && !change.OutageStarted && !change.OutageEnded) return;

        var correlationId = Guid.NewGuid().ToString("N");
        string? failure = result.Failure == ProbeFailure.None ? null : result.Failure.ToString();

        if (change.StatusChanged)
            outbox.Enqueue(new ServiceStatusChangedV1
            {
                Producer = Producer,
                OrganizationId = target.OrganizationId,
                CorrelationId = correlationId,
                ServiceId = target.ServiceId,
                ServiceName = target.Name,
                Environment = target.Environment,
                PreviousStatus = change.Previous.ToString(),
                CurrentStatus = change.Current.ToString(),
                ResponseTimeMs = result.ResponseTimeMs,
                HttpStatusCode = result.HttpStatusCode,
                Failure = failure,
                ChangedAt = result.CheckedAt,
            });

        if (change.OutageStarted)
            outbox.Enqueue(new ServiceDownDetectedV1
            {
                Producer = Producer,
                OrganizationId = target.OrganizationId,
                CorrelationId = correlationId,
                ServiceId = target.ServiceId,
                ServiceName = target.Name,
                Environment = target.Environment,
                Source = source,
                FailureCount = target.ConsecutiveFailures,
                Failure = failure ?? ProbeFailure.UnexpectedStatus.ToString(),
                LastHttpStatusCode = result.HttpStatusCode,
                LastResponseTimeMs = result.ResponseTimeMs,
                DetectedAt = result.CheckedAt,
            });

        if (change.EndedOutageSince is { } downSince)
            outbox.Enqueue(new ServiceRecoveredV1
            {
                Producer = Producer,
                OrganizationId = target.OrganizationId,
                CorrelationId = correlationId,
                ServiceId = target.ServiceId,
                ServiceName = target.Name,
                Environment = target.Environment,
                DownSince = downSince,
                RecoveredAt = result.CheckedAt,
            });
    }
}
