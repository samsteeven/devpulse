using DevPulse.Registry.Application;
using DevPulse.Registry.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DevPulse.Registry.Infrastructure;

public sealed class RegistryDb(DbContextOptions<RegistryDb> options) : DbContext(options)
{
    public DbSet<MonitoredService> Services => Set<MonitoredService>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<MonitoredService>(e =>
        {
            e.ToTable("monitored_services");
            e.Property(s => s.Name).HasMaxLength(100);
            e.Property(s => s.Environment).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.BaseUrl).HasMaxLength(500);
            e.Property(s => s.HealthCheckPath).HasMaxLength(500);
            e.Ignore(s => s.TargetUrl);
            e.HasIndex(s => new { s.OrganizationId, s.Name, s.Environment }).IsUnique();
        });
    }
}

public sealed class RegistryDbDesignTimeFactory : IDesignTimeDbContextFactory<RegistryDb>
{
    public RegistryDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<RegistryDb>()
        .UseNpgsql("Host=design-time").UseSnakeCaseNamingConvention().Options);
}

public sealed class MonitoredServiceRepository(RegistryDb db) : IMonitoredServiceRepository
{
    public async Task<IReadOnlyList<MonitoredService>> ListAsync(Guid organizationId, CancellationToken ct) =>
        await db.Services.AsNoTracking().Where(s => s.OrganizationId == organizationId).OrderBy(s => s.Name).ToListAsync(ct);

    public Task<MonitoredService?> FindAsync(Guid organizationId, Guid id, CancellationToken ct) =>
        db.Services.FirstOrDefaultAsync(s => s.OrganizationId == organizationId && s.Id == id, ct);

    public async Task<IReadOnlyList<MonitoredService>> ListEnabledAcrossOrganizationsAsync(CancellationToken ct) =>
        await db.Services.AsNoTracking().Where(s => s.Enabled).ToListAsync(ct);

    public void Add(MonitoredService service) => db.Services.Add(service);

    public void Remove(MonitoredService service) => db.Services.Remove(service);

    public async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DuplicateServiceException();
        }
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddRegistryInfrastructure(this IServiceCollection services)
    {
        services.AddDbContext<RegistryDb>((provider, options) => options
            .UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("Database"))
            .UseSnakeCaseNamingConvention());

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IMonitoredServiceRepository, MonitoredServiceRepository>();
        services.AddScoped<ServiceCatalog>();
        return services;
    }
}
