using DevPulse.Incident.Application;
using DevPulse.Incident.Domain;
using DevPulse.Security.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DevPulse.Incident.Api.Controllers;

/// <summary>Un incident d'une autre organisation répond 404, comme s'il n'existait pas.</summary>
[ApiController]
[Route("api/incidents")]
public sealed class IncidentsController(IncidentService incidents) : ControllerBase
{
    private Actor Actor => new(User.GetUserId(), User.GetEmail());

    [HttpGet]
    public Task<IReadOnlyList<IncidentSummary>> List(
        [FromQuery] IncidentFilter status = IncidentFilter.All, [FromQuery] Guid? serviceId = null, [FromQuery] int limit = 50, CancellationToken ct = default) =>
        incidents.ListAsync(User.GetOrganizationId(), status, serviceId, limit, ct);

    [HttpGet("stats")]
    public Task<IncidentStatsResponse> Stats(CancellationToken ct) => incidents.StatsAsync(User.GetOrganizationId(), ct);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<IncidentDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        await incidents.GetAsync(User.GetOrganizationId(), id, ct) is { } incident ? Ok(incident) : NotFound();

    [Authorize(Policies.ManageIncidents)]
    [HttpPost("{id:guid}/acknowledge")]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct) =>
        await incidents.AcknowledgeAsync(User.GetOrganizationId(), id, Actor, ct) is { } incident ? Ok(incident) : NotFound();

    [Authorize(Policies.ManageIncidents)]
    [HttpPost("{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, ResolveRequest? request, CancellationToken ct) =>
        await incidents.ResolveAsync(User.GetOrganizationId(), id, Actor, request?.Note, ct) is { } incident ? Ok(incident) : NotFound();

    [Authorize(Policies.ManageIncidents)]
    [HttpPost("{id:guid}/notes")]
    public async Task<IActionResult> AddNote(Guid id, NoteRequest request, CancellationToken ct) =>
        await incidents.AddNoteAsync(User.GetOrganizationId(), id, Actor, request.Text, ct) is { } incident ? Ok(incident) : NotFound();
}
