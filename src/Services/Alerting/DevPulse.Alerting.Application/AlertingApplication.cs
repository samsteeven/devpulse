using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using DevPulse.Alerting.Domain;
using DevPulse.Contracts.Events.Incident;
using DevPulse.Contracts.Events.Monitoring;
using DevPulse.Contracts.Messaging;
using DevPulse.Security.Ssrf;
using Microsoft.Extensions.Options;

namespace DevPulse.Alerting.Application;

public sealed record ChannelRequest(
    [Required] string Name,
    [Required] ChannelKind Kind,
    [Required] string Url,
    [Required] IReadOnlyList<AlertEvent> Events,
    IReadOnlyList<string>? Environments = null,
    bool Enabled = true);

public sealed record ChannelResponse(
    Guid Id, string Name, string Kind, string Url, IReadOnlyList<string> Events, IReadOnlyList<string> Environments,
    bool Enabled, DateTimeOffset CreatedAt, string? SigningSecret);

public sealed record DeliveryResponse(
    Guid Id, Guid ChannelId, string ChannelName, Guid IncidentId, string Event, string ServiceName, string Status,
    int Attempts, int? LastStatusCode, string? LastError, DateTimeOffset OccurredAt, DateTimeOffset? SentAt, int? LatencyMs);

public sealed record TestResult(bool Success, int? StatusCode, string? Error);

/// <summary>Résultat d'un envoi : un code 2xx est un succès, le reste est repris.</summary>
public sealed record SendResult(bool Success, int? StatusCode, string? Error);

public sealed record OutgoingAlert(Guid DeliveryId, ChannelKind Kind, string Url, string SigningSecret, AlertEvent Event, string Payload);

public interface IAlertSender
{
    Task<SendResult> SendAsync(OutgoingAlert alert, CancellationToken ct);
}

/// <summary>Notifications poussées aux navigateurs connectés de l'organisation.</summary>
public interface IRealtimeNotifier
{
    Task ServiceStatusChangedAsync(Guid organizationId, ServiceStatusNotification notification, CancellationToken ct);
    Task IncidentChangedAsync(Guid organizationId, IncidentNotification notification, CancellationToken ct);
}

public sealed record ServiceStatusNotification(Guid ServiceId, string ServiceName, string PreviousStatus, string CurrentStatus, DateTimeOffset ChangedAt);
public sealed record IncidentNotification(Guid IncidentId, Guid ServiceId, string ServiceName, string Change, string Severity, string Title, DateTimeOffset At);

public interface IAlertChannelRepository
{
    Task<IReadOnlyList<AlertChannel>> ListAsync(Guid organizationId, CancellationToken ct);
    Task<AlertChannel?> FindAsync(Guid organizationId, Guid id, CancellationToken ct);
    void Add(AlertChannel channel);
    void Remove(AlertChannel channel);
    Task SaveAsync(CancellationToken ct);
}

public interface IDeliveryRepository
{
    Task<IReadOnlyList<(AlertDelivery Delivery, string ChannelName)>> ListAsync(Guid organizationId, int limit, CancellationToken ct);
    void Add(AlertDelivery delivery);
}

/// <summary>Réveille l'expéditeur dès qu'un envoi est enregistré.</summary>
public sealed class DeliverySignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        if (_semaphore.CurrentCount == 0)
        {
            try { _semaphore.Release(); } catch (SemaphoreFullException) { }
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);
}

public sealed class AlertChannelService(
    IAlertChannelRepository channels, IDeliveryRepository deliveries, IAlertSender sender,
    IOptions<TargetPolicyOptions> policy, TimeProvider clock)
{
    public async Task<IReadOnlyList<ChannelResponse>> ListAsync(Guid organizationId, CancellationToken ct) =>
        (await channels.ListAsync(organizationId, ct)).Select(c => ToResponse(c, revealSecret: false)).ToList();

    public async Task<ChannelResponse> CreateAsync(Guid organizationId, ChannelRequest request, CancellationToken ct)
    {
        var url = ValidateUrl(request.Url);
        var channel = AlertChannel.Create(organizationId, request.Name, request.Kind, url, request.Events, request.Environments ?? [], clock.GetUtcNow());
        channels.Add(channel);
        await channels.SaveAsync(ct);
        return ToResponse(channel, revealSecret: true);
    }

    public async Task<ChannelResponse?> UpdateAsync(Guid organizationId, Guid id, ChannelRequest request, CancellationToken ct)
    {
        var channel = await channels.FindAsync(organizationId, id, ct);
        if (channel is null) return null;
        if (channel.Kind != request.Kind) throw new DomainException("Le type d'un canal ne change pas : créez-en un autre.");

        channel.Update(request.Name, ValidateUrl(request.Url), request.Events, request.Environments ?? [], request.Enabled, clock.GetUtcNow());
        await channels.SaveAsync(ct);
        return ToResponse(channel, revealSecret: false);
    }

    public async Task<bool> DeleteAsync(Guid organizationId, Guid id, CancellationToken ct)
    {
        var channel = await channels.FindAsync(organizationId, id, ct);
        if (channel is null) return false;
        channels.Remove(channel);
        await channels.SaveAsync(ct);
        return true;
    }

    /// <summary>Envoi immédiat d'un message d'essai, sans file ni reprise : la réponse dit tout de suite si l'URL fonctionne.</summary>
    public async Task<TestResult?> TestAsync(Guid organizationId, Guid id, CancellationToken ct)
    {
        var channel = await channels.FindAsync(organizationId, id, ct);
        if (channel is null) return null;

        var payload = AlertMessages.Build(channel.Kind, AlertEvent.IncidentCreated, new AlertFacts(
            Guid.Empty, "Service de test", "Production", "Minor", "Message d'essai envoyé depuis DevPulse", clock.GetUtcNow(), null));
        var result = await sender.SendAsync(new OutgoingAlert(Guid.NewGuid(), channel.Kind, channel.Url, channel.SigningSecret, AlertEvent.IncidentCreated, payload), ct);
        return new TestResult(result.Success, result.StatusCode, result.Error);
    }

    public async Task<IReadOnlyList<DeliveryResponse>> ListDeliveriesAsync(Guid organizationId, int limit, CancellationToken ct) =>
        (await deliveries.ListAsync(organizationId, Math.Clamp(limit, 1, 200), ct))
            .Select(d => new DeliveryResponse(
                d.Delivery.Id, d.Delivery.ChannelId, d.ChannelName, d.Delivery.IncidentId, d.Delivery.Event.ToString(), d.Delivery.ServiceName,
                d.Delivery.Status.ToString(), d.Delivery.Attempts, d.Delivery.LastStatusCode, d.Delivery.LastError,
                d.Delivery.OccurredAt, d.Delivery.SentAt, d.Delivery.LatencyMilliseconds))
            .ToList();

    /// <summary>Même contrôle SSRF que pour les services surveillés : un webhook est une URL choisie par l'utilisateur.</summary>
    private string ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) throw new DomainException("L'URL n'est pas valide.");
        if (TargetUrlPolicy.Validate(uri, policy.Value.AllowPrivateTargets) is { } refusal) throw new DomainException(refusal);
        if (!policy.Value.AllowPrivateTargets && uri.Scheme != Uri.UriSchemeHttps) throw new DomainException("Un webhook doit utiliser https.");
        return uri.ToString();
    }

    private static ChannelResponse ToResponse(AlertChannel c, bool revealSecret) => new(
        c.Id, c.Name, c.Kind.ToString(), c.Url, c.Events.Select(e => e.ToString()).ToList(), c.Environments, c.Enabled, c.CreatedAt,
        revealSecret && c.Kind == ChannelKind.Webhook ? c.SigningSecret : null);
}

public sealed record AlertFacts(
    Guid IncidentId, string ServiceName, string Environment, string Severity, string Title, DateTimeOffset OccurredAt, string? Detail);

/// <summary>Le contenu exact envoyé, figé au moment de l'événement : une reprise renvoie le même message.</summary>
public static class AlertMessages
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Build(ChannelKind kind, AlertEvent alertEvent, AlertFacts facts) => kind switch
    {
        ChannelKind.Slack => JsonSerializer.Serialize(new { text = SlackText(alertEvent, facts) }, Json),
        _ => JsonSerializer.Serialize(new
        {
            @event = alertEvent switch
            {
                AlertEvent.IncidentCreated => "incident.created",
                AlertEvent.IncidentAcknowledged => "incident.acknowledged",
                _ => "incident.resolved",
            },
            incidentId = facts.IncidentId,
            serviceName = facts.ServiceName,
            environment = facts.Environment,
            severity = facts.Severity,
            title = facts.Title,
            occurredAt = facts.OccurredAt,
            detail = facts.Detail,
        }, Json),
    };

    private static string SlackText(AlertEvent alertEvent, AlertFacts f) => alertEvent switch
    {
        AlertEvent.IncidentCreated => $":red_circle: *{f.Title}* ({f.Environment}, {f.Severity}){(f.Detail is null ? "" : $" : {f.Detail}")}",
        AlertEvent.IncidentAcknowledged => $":large_yellow_circle: Incident pris en charge : *{f.ServiceName}* ({f.Environment})",
        _ => $":large_green_circle: Incident résolu : *{f.ServiceName}* ({f.Environment}){(f.Detail is null ? "" : $", {f.Detail}")}",
    };
}

/// <summary>
/// Événements d'incident : un envoi par canal concerné, écrit dans la transaction du message, et une notification
/// aux navigateurs. Si le traitement est rejoué après un échec, le navigateur peut recevoir la notification deux fois :
/// elle ne fait que rafraîchir l'affichage, c'est sans conséquence.
/// </summary>
public sealed class IncidentEventsHandler(
    IAlertChannelRepository channels, IDeliveryRepository deliveries, IRealtimeNotifier realtime, DeliverySignal signal, TimeProvider clock)
    : IIntegrationEventHandler<IncidentCreatedV1>, IIntegrationEventHandler<IncidentAcknowledgedV1>,
      IIntegrationEventHandler<IncidentResolvedV1>, IIntegrationEventHandler<IncidentAnalyzedV1>
{
    public Task HandleAsync(IncidentCreatedV1 e, CancellationToken ct)
    {
        var detail = e.HttpStatusCode is { } code ? $"{e.Failure}, HTTP {code}" : e.Failure;
        return DispatchAsync(e, AlertEvent.IncidentCreated, "created", e.DetectedAt, detail, ct);
    }

    public Task HandleAsync(IncidentAcknowledgedV1 e, CancellationToken ct) =>
        DispatchAsync(e, AlertEvent.IncidentAcknowledged, "acknowledged", e.AcknowledgedAt, null, ct);

    public Task HandleAsync(IncidentResolvedV1 e, CancellationToken ct) =>
        DispatchAsync(e, AlertEvent.IncidentResolved, "resolved", e.ResolvedAt,
            $"{(e.Automatic ? "retour automatique" : "clos manuellement")} après {Duration(e.DurationSeconds)}", ct);

    /// <summary>Une analyse n'est pas une alerte : elle rafraîchit seulement l'affichage.</summary>
    public Task HandleAsync(IncidentAnalyzedV1 e, CancellationToken ct) =>
        realtime.IncidentChangedAsync(e.OrganizationId, Notification(e, "analyzed", e.OccurredAt), ct);

    private async Task DispatchAsync(IncidentEventBase e, AlertEvent alertEvent, string change, DateTimeOffset occurredAt, string? detail, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var facts = new AlertFacts(e.IncidentId, e.ServiceName, e.Environment, e.Severity, e.Title, occurredAt, detail);

        var queued = 0;
        foreach (var channel in await channels.ListAsync(e.OrganizationId, ct))
        {
            if (!channel.Accepts(alertEvent, e.Environment)) continue;
            deliveries.Add(AlertDelivery.Create(channel, e.IncidentId, alertEvent, e.ServiceName, AlertMessages.Build(channel.Kind, alertEvent, facts), occurredAt, now));
            queued++;
        }

        if (queued > 0) signal.Notify();
        await realtime.IncidentChangedAsync(e.OrganizationId, Notification(e, change, occurredAt), ct);
    }

    private static IncidentNotification Notification(IncidentEventBase e, string change, DateTimeOffset at) =>
        new(e.IncidentId, e.ServiceId, e.ServiceName, change, e.Severity, e.Title, at);

    private static string Duration(int seconds) =>
        seconds < 120 ? $"{seconds} s" : seconds < 7200 ? $"{seconds / 60} min" : $"{seconds / 3600} h {seconds % 3600 / 60} min";
}

public sealed class ServiceStatusHandler(IRealtimeNotifier realtime) : IIntegrationEventHandler<ServiceStatusChangedV1>
{
    public Task HandleAsync(ServiceStatusChangedV1 e, CancellationToken ct) =>
        realtime.ServiceStatusChangedAsync(e.OrganizationId,
            new ServiceStatusNotification(e.ServiceId, e.ServiceName, e.PreviousStatus, e.CurrentStatus, e.ChangedAt), ct);
}
