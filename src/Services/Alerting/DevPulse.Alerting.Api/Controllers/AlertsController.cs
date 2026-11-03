using DevPulse.Alerting.Application;
using DevPulse.Security.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DevPulse.Alerting.Api.Controllers;

[ApiController]
[Route("api/alert-channels")]
public sealed class AlertChannelsController(AlertChannelService channels) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ChannelResponse>> List(CancellationToken ct) => channels.ListAsync(User.GetOrganizationId(), ct);

    /// <summary>La clé de signature n'apparaît que dans cette réponse.</summary>
    [Authorize(Policies.ManageAlerts)]
    [HttpPost]
    [ProducesResponseType<ChannelResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(ChannelRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await channels.CreateAsync(User.GetOrganizationId(), request, ct));

    [Authorize(Policies.ManageAlerts)]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, ChannelRequest request, CancellationToken ct) =>
        await channels.UpdateAsync(User.GetOrganizationId(), id, request, ct) is { } channel ? Ok(channel) : NotFound();

    [Authorize(Policies.ManageAlerts)]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await channels.DeleteAsync(User.GetOrganizationId(), id, ct) ? NoContent() : NotFound();

    [Authorize(Policies.ManageAlerts)]
    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct) =>
        await channels.TestAsync(User.GetOrganizationId(), id, ct) is { } result ? Ok(result) : NotFound();
}

[ApiController]
[Route("api/alert-deliveries")]
public sealed class AlertDeliveriesController(AlertChannelService channels) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<DeliveryResponse>> List([FromQuery] int limit = 50, CancellationToken ct = default) =>
        channels.ListDeliveriesAsync(User.GetOrganizationId(), limit, ct);
}
