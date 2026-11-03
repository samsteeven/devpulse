using DevPulse.Monitoring.Application;
using DevPulse.Monitoring.Domain;
using DevPulse.Security.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DevPulse.Monitoring.Api.Controllers;

[ApiController]
[Route("api/agents")]
public sealed class AgentsController(IAgentStore agents, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<AgentResponse>> List(CancellationToken ct) => agents.ListAsync(User.GetOrganizationId(), ct);

    /// <summary>La clé n'apparaît que dans cette réponse : à copier dans la configuration de l'agent.</summary>
    [Authorize(Policies.ManageServices)]
    [HttpPost]
    [ProducesResponseType<AgentCreated>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Register(AgentRegistrationRequest request, CancellationToken ct)
    {
        try
        {
            return StatusCode(StatusCodes.Status201Created, await agents.RegisterAsync(User.GetOrganizationId(), request, ct));
        }
        catch (AgentException e)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: e.Message);
        }
    }

    [Authorize(Policies.ManageServices)]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) =>
        await agents.RevokeAsync(User.GetOrganizationId(), id, ct) ? NoContent() : NotFound();

    [HttpGet("{id:guid}/samples")]
    public async Task<IActionResult> Samples(Guid id, [FromQuery] int hours = 6, CancellationToken ct = default) =>
        await agents.SamplesAsync(User.GetOrganizationId(), id, clock.GetUtcNow().AddHours(-Math.Clamp(hours, 1, 168)), ct) is { } samples
            ? Ok(samples)
            : NotFound();

    /// <summary>
    /// Battement d'un agent. Pas de jeton utilisateur ici : l'agent présente sa propre clé (« Authorization: Agent dpa_… »),
    /// et l'organisation est celle de la clé. Clé fausse, inconnue ou révoquée : 401, sans distinction.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("ingest")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Ingest(AgentReportRequest report, CancellationToken ct)
    {
        var header = Request.Headers.Authorization.ToString();
        var key = header.StartsWith("Agent ", StringComparison.Ordinal) ? header["Agent ".Length..].Trim() : null;

        try
        {
            return await agents.IngestAsync(key, report, ct) == IngestOutcome.Accepted
                ? Accepted()
                : Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Clé d'agent invalide ou révoquée.");
        }
        catch (AgentException e)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: e.Message);
        }
    }
}
