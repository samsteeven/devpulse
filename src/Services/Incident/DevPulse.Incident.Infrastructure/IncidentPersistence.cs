using DevPulse.Contracts.Events.AI;
using DevPulse.Contracts.Events.Monitoring;
using DevPulse.Contracts.Messaging;
using DevPulse.Incident.Application;
using DevPulse.Incident.Domain;
using DevPulse.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using IncidentEntity = DevPulse.Incident.Domain.Incident;

namespace DevPulse.Incident.Infrastructure;

public sealed class IncidentDb(DbContextOptions<IncidentDb> options) : DbContext(options)
{
    public DbSet<IncidentEntity> Incidents => Set<IncidentEntity>();
    public DbSet<TimelineEntry> Timeline => Set<TimelineEntry>();
    public DbSet<ServiceOutageState> OutageStates => Set<ServiceOutageState>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.AddMessagingTables();

        model.Entity<IncidentEntity>(e =>
        {
            e.ToTable("incidents");
            e.Property(i => i.Id).ValueGeneratedNever();
            e.Property(i => i.ServiceName).HasMaxLength(100);
            e.Property(i => i.Environment).HasMaxLength(20);
            e.Property(i => i.Title).HasMaxLength(200);
            e.Property(i => i.Source).HasMaxLength(20);
            e.Property(i => i.Failure).HasMaxLength(40);
            e.Property(i => i.Severity).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.AnalysisProvider).HasMaxLength(20);
            e.Property(i => i.AnalysisModel).HasMaxLength(100);
            e.Property(i => i.AnalysisSummary).HasMaxLength(2000);
            e.Property(i => i.ProbableCauses).HasColumnType("jsonb");
            e.Property(i => i.SuggestedActions).HasColumnType("jsonb");
            e.Ignore(i => i.IsActive);
            e.HasMany(i => i.Timeline).WithOne().HasForeignKey(t => t.IncidentId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(i => i.Timeline).HasField("_timeline").UsePropertyAccessMode(PropertyAccessMode.Field);
            e.HasIndex(i => new { i.OrganizationId, i.OpenedAt });
            // Au plus un incident en cours par service : deux pannes simultanées pour le même service sont impossibles.
            e.HasIndex(i => i.ServiceId).IsUnique().HasFilter("status <> 'Resolved'");
        });

        model.Entity<TimelineEntry>(e =>
        {
            e.ToTable("incident_timeline");
            e.Property(t => t.Id).ValueGeneratedNever();
            e.Property(t => t.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.AuthorEmail).HasMaxLength(254);
            e.Property(t => t.Message).HasMaxLength(2100);
            e.HasIndex(t => new { t.IncidentId, t.At });
        });

        model.Entity<ServiceOutageState>(e =>
        {
            e.ToTable("service_outage_states");
            e.HasKey(s => s.ServiceId);
            e.Property(s => s.ServiceId).ValueGeneratedNever();
        });
    }
}

public sealed class IncidentDbDesignTimeFactory : IDesignTimeDbContextFactory<IncidentDb>
{
    public IncidentDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<IncidentDb>()
        .UseNpgsql("Host=design-time").UseSnakeCaseNamingConvention().Options);
}

public sealed class IncidentRepository(IncidentDb db) : IIncidentRepository
{
    public async Task<IReadOnlyList<IncidentEntity>> ListAsync(Guid organizationId, IncidentFilter filter, Guid? serviceId, int limit, CancellationToken ct)
    {
        var query = db.Incidents.AsNoTracking().Where(i => i.OrganizationId == organizationId);
        if (serviceId is { } id) query = query.Where(i => i.ServiceId == id);
        query = filter switch
        {
            IncidentFilter.Active => query.Where(i => i.Status != IncidentStatus.Resolved),
            IncidentFilter.Resolved => query.Where(i => i.Status == IncidentStatus.Resolved),
            _ => query,
        };
        return await query.OrderByDescending(i => i.OpenedAt).Take(limit).ToListAsync(ct);
    }

    public Task<IncidentEntity?> FindAsync(Guid organizationId, Guid id, CancellationToken ct) =>
        db.Incidents.Include(i => i.Timeline).FirstOrDefaultAsync(i => i.OrganizationId == organizationId && i.Id == id, ct);

    public Task<IncidentEntity?> FindActiveForServiceAsync(Guid organizationId, Guid serviceId, CancellationToken ct) =>
        db.Incidents.Include(i => i.Timeline)
            .FirstOrDefaultAsync(i => i.OrganizationId == organizationId && i.ServiceId == serviceId && i.Status != IncidentStatus.Resolved, ct);

    public Task<IncidentEntity?> FindByIdForEventAsync(Guid organizationId, Guid id, CancellationToken ct) => FindAsync(organizationId, id, ct);

    public async Task<ServiceOutageState> GetOutageStateAsync(Guid organizationId, Guid serviceId, CancellationToken ct)
    {
        var state = await db.OutageStates.FirstOrDefaultAsync(s => s.ServiceId == serviceId && s.OrganizationId == organizationId, ct);
        if (state is not null) return state;

        state = ServiceOutageState.For(serviceId, organizationId);
        db.OutageStates.Add(state);
        return state;
    }

    public async Task<IncidentStatsResponse> StatsAsync(Guid organizationId, DateTimeOffset since, CancellationToken ct)
    {
        var mine = db.Incidents.AsNoTracking().Where(i => i.OrganizationId == organizationId);
        var open = await mine.CountAsync(i => i.Status == IncidentStatus.Open, ct);
        var acknowledged = await mine.CountAsync(i => i.Status == IncidentStatus.Acknowledged, ct);
        var resolved = await mine.Where(i => i.Status == IncidentStatus.Resolved && i.ResolvedAt >= since)
            .Select(i => new { i.DetectedAt, i.ResolvedAt }).ToListAsync(ct);

        int? mttr = resolved.Count == 0 ? null : (int)resolved.Average(r => (r.ResolvedAt!.Value - r.DetectedAt).TotalSeconds);
        return new IncidentStatsResponse(open, acknowledged, resolved.Count, mttr, (int)Math.Round((DateTimeOffset.UtcNow - since).TotalDays));
    }

    public void Add(IncidentEntity incident) => db.Incidents.Add(incident);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public static class DependencyInjection
{
    public static IServiceCollection AddIncidentInfrastructure(this IServiceCollection services)
    {
        services.AddDbContext<IncidentDb>((provider, options) => options
            .UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("Database"))
            .UseSnakeCaseNamingConvention());

        services.AddScoped<IIncidentRepository, IncidentRepository>();
        services.AddScoped<IncidentService>();

        services.AddDevPulseMessaging("incident-service")
            .AddOutbox<IncidentDb>()
            .AddConsumer<IncidentDb>(EventTopology.IncidentServiceState, c => c
                .Handle<ServiceDownDetectedV1, ServiceDownDetectedHandler>()
                .Handle<ServiceRecoveredV1, ServiceRecoveredHandler>())
            .AddConsumer<IncidentDb>(EventTopology.IncidentAiAnalysis, c => c
                .Handle<AnalysisCompletedV1, AnalysisCompletedHandler>());
        return services;
    }
}
