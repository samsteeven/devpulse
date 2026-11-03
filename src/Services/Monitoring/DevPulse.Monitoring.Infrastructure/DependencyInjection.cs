using System.Net.Http.Json;
using DevPulse.Contracts.Internal;
using DevPulse.Monitoring.Application;
using DevPulse.Security.Authentication;
using DevPulse.Security.Ssrf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DevPulse.Monitoring.Infrastructure;

public sealed class RegistryClient(HttpClient http) : IRegistryClient
{
    public async Task<IReadOnlyList<ActiveServiceDto>> GetActiveServicesAsync(CancellationToken ct) =>
        await http.GetFromJsonAsync<List<ActiveServiceDto>>("internal/services/active", ct) ?? [];
}

public static class DependencyInjection
{
    /// <summary>Base de données et lectures : suffisant pour l'API.</summary>
    public static IServiceCollection AddMonitoringPersistence(this IServiceCollection services)
    {
        services.AddDbContext<MonitoringDb>((provider, options) => options
            .UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("Database"))
            .UseSnakeCaseNamingConvention());

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IHealthReadStore, HealthReadStore>();
        services.AddScoped<ITargetStore, TargetStore>();
        services.AddScoped<IAgentStore, AgentStore>();
        services.AddScoped<HealthQueries>();
        return services;
    }

    /// <summary>Sondes et accès à Registry : seul le worker sort sur le réseau.</summary>
    public static IServiceCollection AddMonitoringProbing(this IServiceCollection services)
    {
        services.AddOptions<TargetPolicyOptions>().BindConfiguration(TargetPolicyOptions.SectionName);
        services.AddOptions<InternalApiOptions>().BindConfiguration(InternalApiOptions.SectionName);

        services.AddHttpClient(HttpTargetProber.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(provider =>
                HttpTargetProber.CreateHandler(provider.GetRequiredService<IOptions<TargetPolicyOptions>>().Value));
        services.AddSingleton<ITargetProber, HttpTargetProber>();

        services.AddHttpClient<IRegistryClient, RegistryClient>((provider, client) =>
        {
            client.BaseAddress = new Uri(provider.GetRequiredService<IConfiguration>()["Registry:BaseUrl"]
                ?? throw new InvalidOperationException("Registry:BaseUrl est obligatoire."));
            client.DefaultRequestHeaders.Add(InternalApiOptions.HeaderName,
                provider.GetRequiredService<IOptions<InternalApiOptions>>().Value.Key);
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        return services;
    }
}
