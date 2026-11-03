using System.ComponentModel.DataAnnotations;
using DevPulse.Contracts.Internal;
using DevPulse.Registry.Domain;
using DevPulse.Security.Ssrf;
using Microsoft.Extensions.Options;

namespace DevPulse.Registry.Application;

public sealed record ServiceRequest(
    [Required] string Name,
    [Required] ServiceEnvironment Environment,
    [Required] string BaseUrl,
    string HealthCheckPath = "/health",
    int CheckIntervalSeconds = 60,
    int TimeoutSeconds = 10,
    bool Enabled = true);

public sealed record ServiceResponse(
    Guid Id, string Name, string Environment, string BaseUrl, string HealthCheckPath,
    int CheckIntervalSeconds, int TimeoutSeconds, bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed class DuplicateServiceException() : Exception("Un service de ce nom existe déjà dans cet environnement.");

/// <summary>Toutes les opérations prennent l'organisation en paramètre : aucune lecture ne peut l'oublier.</summary>
public interface IMonitoredServiceRepository
{
    Task<IReadOnlyList<MonitoredService>> ListAsync(Guid organizationId, CancellationToken ct);
    Task<MonitoredService?> FindAsync(Guid organizationId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<MonitoredService>> ListEnabledAcrossOrganizationsAsync(CancellationToken ct);
    void Add(MonitoredService service);
    void Remove(MonitoredService service);

    /// <exception cref="DuplicateServiceException">Le couple organisation, nom et environnement existe déjà.</exception>
    Task SaveAsync(CancellationToken ct);
}

public sealed class ServiceCatalog(IMonitoredServiceRepository repository, IOptions<TargetPolicyOptions> policy, TimeProvider clock)
{
    public async Task<IReadOnlyList<ServiceResponse>> ListAsync(Guid organizationId, CancellationToken ct) =>
        (await repository.ListAsync(organizationId, ct)).Select(ToResponse).ToList();

    public async Task<ServiceResponse?> GetAsync(Guid organizationId, Guid id, CancellationToken ct) =>
        await repository.FindAsync(organizationId, id, ct) is { } service ? ToResponse(service) : null;

    public async Task<ServiceResponse> CreateAsync(Guid organizationId, ServiceRequest request, CancellationToken ct)
    {
        var service = MonitoredService.Create(
            organizationId, request.Name, request.Environment, request.BaseUrl,
            request.HealthCheckPath, request.CheckIntervalSeconds, request.TimeoutSeconds, clock.GetUtcNow());
        EnsureAllowedTarget(service);

        repository.Add(service);
        await repository.SaveAsync(ct);
        return ToResponse(service);
    }

    public async Task<ServiceResponse?> UpdateAsync(Guid organizationId, Guid id, ServiceRequest request, CancellationToken ct)
    {
        var service = await repository.FindAsync(organizationId, id, ct);
        if (service is null) return null;

        service.Update(
            request.Name, request.Environment, request.BaseUrl, request.HealthCheckPath,
            request.CheckIntervalSeconds, request.TimeoutSeconds, request.Enabled, clock.GetUtcNow());
        EnsureAllowedTarget(service);

        await repository.SaveAsync(ct);
        return ToResponse(service);
    }

    public async Task<bool> DeleteAsync(Guid organizationId, Guid id, CancellationToken ct)
    {
        var service = await repository.FindAsync(organizationId, id, ct);
        if (service is null) return false;

        repository.Remove(service);
        await repository.SaveAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<ActiveServiceDto>> ListActiveAsync(CancellationToken ct) =>
        (await repository.ListEnabledAcrossOrganizationsAsync(ct))
            .Select(s => new ActiveServiceDto(
                s.Id, s.OrganizationId, s.Name, s.Environment.ToString(), s.TargetUrl, s.CheckIntervalSeconds, s.TimeoutSeconds))
            .ToList();

    private void EnsureAllowedTarget(MonitoredService service)
    {
        if (!Uri.TryCreate(service.BaseUrl, UriKind.Absolute, out var baseUri) || !Uri.TryCreate(service.TargetUrl, UriKind.Absolute, out var target))
            throw new DomainException("L'URL de base n'est pas valide.");

        if (target.Host != baseUri.Host) throw new DomainException("Le chemin de vérification ne doit pas changer d'hôte.");

        if (TargetUrlPolicy.Validate(target, policy.Value.AllowPrivateTargets) is { } refusal)
            throw new DomainException(refusal);
    }

    private static ServiceResponse ToResponse(MonitoredService s) => new(
        s.Id, s.Name, s.Environment.ToString(), s.BaseUrl, s.HealthCheckPath,
        s.CheckIntervalSeconds, s.TimeoutSeconds, s.Enabled, s.CreatedAt, s.UpdatedAt);
}
