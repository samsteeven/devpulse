using DevPulse.Contracts.Internal;
using DevPulse.Registry.Application;
using DevPulse.Security.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DevPulse.Registry.Api.Controllers;

/// <summary>Une ressource d'une autre organisation répond 404, pas 403 : son existence n'est pas révélée.</summary>
[ApiController]
[Route("api/services")]
public sealed class ServicesController(ServiceCatalog catalog) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ServiceResponse>> List(CancellationToken ct) =>
        catalog.ListAsync(User.GetOrganizationId(), ct);

    [HttpGet("{id:guid}", Name = nameof(GetById))]
    [ProducesResponseType<ServiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct) =>
        await catalog.GetAsync(User.GetOrganizationId(), id, ct) is { } service ? Ok(service) : NotFound();

    [Authorize(Policies.ManageServices)]
    [HttpPost]
    [ProducesResponseType<ServiceResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(ServiceRequest request, CancellationToken ct)
    {
        var service = await catalog.CreateAsync(User.GetOrganizationId(), request, ct);
        return CreatedAtRoute(nameof(GetById), new { id = service.Id }, service);
    }

    [Authorize(Policies.ManageServices)]
    [HttpPut("{id:guid}")]
    [ProducesResponseType<ServiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, ServiceRequest request, CancellationToken ct) =>
        await catalog.UpdateAsync(User.GetOrganizationId(), id, request, ct) is { } service ? Ok(service) : NotFound();

    [Authorize(Policies.ManageServices)]
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await catalog.DeleteAsync(User.GetOrganizationId(), id, ct) ? NoContent() : NotFound();
}

/// <summary>Route réservée au worker de monitoring. La passerelle ne l'expose pas.</summary>
[ApiController]
[Route("internal/services")]
[RequireInternalKey]
public sealed class InternalServicesController(ServiceCatalog catalog) : ControllerBase
{
    [HttpGet("active")]
    public Task<IReadOnlyList<ActiveServiceDto>> Active(CancellationToken ct) => catalog.ListActiveAsync(ct);
}
