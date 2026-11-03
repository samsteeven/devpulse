using DevPulse.Alerting.Application;
using DevPulse.Security.Authentication;
using Microsoft.AspNetCore.SignalR;

namespace DevPulse.Alerting.Api;

/// <summary>
/// Hub du temps réel. Chaque connexion rejoint le groupe de son organisation, lue dans le jeton : un client ne peut
/// pas demander à rejoindre un autre groupe, il n'existe aucune méthode pour cela.
/// </summary>
public sealed class NotificationsHub : Hub
{
    public static string GroupOf(Guid organizationId) => $"org:{organizationId:N}";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupOf(Context.User!.GetOrganizationId()));
        await base.OnConnectedAsync();
    }
}

public sealed class SignalRNotifier(IHubContext<NotificationsHub> hub) : IRealtimeNotifier
{
    public Task ServiceStatusChangedAsync(Guid organizationId, ServiceStatusNotification notification, CancellationToken ct) =>
        hub.Clients.Group(NotificationsHub.GroupOf(organizationId)).SendAsync("serviceStatusChanged", notification, ct);

    public Task IncidentChangedAsync(Guid organizationId, IncidentNotification notification, CancellationToken ct) =>
        hub.Clients.Group(NotificationsHub.GroupOf(organizationId)).SendAsync("incidentChanged", notification, ct);
}
