using DevPulse.Monitoring.Application;
using DevPulse.Security.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace DevPulse.Monitoring.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class HealthController(HealthQueries queries) : ControllerBase
{
    [HttpGet("dashboard/overview")]
    public Task<OverviewResponse> Overview(CancellationToken ct) =>
        queries.GetOverviewAsync(User.GetOrganizationId(), ct);

    [HttpGet("services/{id:guid}/health")]
    [ProducesResponseType<ServiceHealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Health(Guid id, CancellationToken ct) =>
        await queries.GetHealthAsync(User.GetOrganizationId(), id, ct) is { } health ? Ok(health) : NotFound();

    [HttpGet("services/{id:guid}/metrics")]
    [ProducesResponseType<ServiceMetricsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Metrics(Guid id, [FromQuery] int hours = 24, CancellationToken ct = default) =>
        await queries.GetMetricsAsync(User.GetOrganizationId(), id, hours, ct) is { } metrics ? Ok(metrics) : NotFound();
}
