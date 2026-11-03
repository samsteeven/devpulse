using System.Diagnostics;
using System.Text;
using DevPulse.Contracts.Events.Common;
using DevPulse.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DevPulse.Messaging;

public sealed record EventRoute(Type EventType, Func<IServiceProvider, IntegrationEvent, CancellationToken, Task> Invoke);

public sealed class ConsumerRegistration(string queue, Type? dbContextType)
{
    public string Queue { get; } = queue;

    /// <summary>Base du service : le traitement et la trace « déjà traité » partent dans la même transaction.</summary>
    public Type? DbContextType { get; } = dbContextType;

    public Dictionary<string, EventRoute> Routes { get; } = [];
}

/// <summary>
/// Consomme une file. Avec une base : l'identifiant de l'événement est inscrit dans <c>processed_messages</c> dans la
/// transaction du traitement, et un doublon est acquitté sans effet. L'acquittement n'a lieu qu'après validation.
/// En cas d'échec : nouvelle tentative après un délai (file de reprise), puis file morte avec la dernière erreur.
/// </summary>
public sealed class QueueConsumer(
    ConsumerRegistration registration, IServiceScopeFactory scopes, RabbitMqConnection connection,
    IOptions<RabbitMqOptions> options, TimeProvider clock, ILogger<QueueConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled) return;

        try
        {
            var publish = await connection.CreatePublishChannelAsync(stoppingToken);
            var amqp = await connection.GetAsync(stoppingToken);
            var channel = await amqp.CreateChannelAsync(cancellationToken: stoppingToken);
            await channel.BasicQosAsync(0, settings.Prefetch, false, stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) => OnDeliveryAsync(channel, publish, delivery, settings, stoppingToken);
            await channel.BasicConsumeAsync(registration.Queue, autoAck: false, consumer, stoppingToken);
            logger.LogInformation("Consommation de {Queue} ({Count} type(s) d'événement)", registration.Queue, registration.Routes.Count);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Les messages non acquittés reviennent dans la file à la fermeture du canal.
        }
    }

    private async Task OnDeliveryAsync(IChannel channel, IChannel publish, BasicDeliverEventArgs delivery, RabbitMqOptions settings, CancellationToken ct)
    {
        var headers = delivery.BasicProperties.Headers;
        var routingKey = Headers.ReadString(headers, Headers.RoutingKey) ?? delivery.RoutingKey;
        var attempt = Headers.ReadInt(headers, Headers.Attempt, 1);
        var body = delivery.Body.ToArray();

        using var activity = MessagingTelemetry.Source.StartActivity(
            $"{registration.Queue} process", ActivityKind.Consumer, MessagingTelemetry.ParentFrom(Headers.ReadString(headers, Headers.TraceParent)));
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", registration.Queue);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", routingKey);
        activity?.SetTag("messaging.message.id", delivery.BasicProperties.MessageId);

        try
        {
            if (!registration.Routes.TryGetValue(routingKey, out var route))
            {
                await DeadLetterAsync(channel, publish, delivery, body, attempt, $"Aucun traitement pour {routingKey}", ct);
                Count("dead_lettered");
                return;
            }

            IntegrationEvent? integrationEvent;
            try
            {
                integrationEvent = EventSerializer.Deserialize(body, route.EventType);
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
            {
                integrationEvent = null;
                logger.LogError(e, "Message illisible sur {Queue}", registration.Queue);
            }

            // Un message illisible ne le sera pas davantage plus tard : file morte directement.
            if (integrationEvent is null)
            {
                await DeadLetterAsync(channel, publish, delivery, body, attempt, "Message illisible", ct);
                Count("dead_lettered");
                return;
            }

            using var scope = logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = integrationEvent.CorrelationId,
                ["EventId"] = integrationEvent.EventId,
            });

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var processed = await ProcessAsync(route, integrationEvent, publish, ct);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
                MessagingMetrics.ProcessingDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    MessagingMetrics.Tag("queue", registration.Queue));
                Count(processed ? "processed" : "duplicate");
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, e.Message);
                if (attempt >= settings.MaxAttempts)
                {
                    logger.LogError(e, "Événement {EventId} en échec après {Attempt} tentative(s) : file morte", integrationEvent.EventId, attempt);
                    await DeadLetterAsync(channel, publish, delivery, body, attempt, $"{e.GetType().Name}: {e.Message}", ct);
                    Count("dead_lettered");
                }
                else
                {
                    logger.LogWarning(e, "Événement {EventId} en échec (tentative {Attempt}) ; reprise dans {Delay} s",
                        integrationEvent.EventId, attempt, settings.RetryDelaySeconds);
                    await RepublishAsync(channel, publish, delivery, body, Topology.RetryQueue(registration.Queue), attempt + 1, null, ct);
                    Count("retried");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Arrêt : pas d'acquittement, le message sera redistribué.
        }
        catch (Exception e)
        {
            // Le courtier refuse la copie de reprise : on rend le message à la file plutôt que de le perdre.
            logger.LogError(e, "Reprise impossible pour un message de {Queue} ; il est remis dans la file", registration.Queue);
            if (channel.IsOpen) await channel.BasicNackAsync(delivery.DeliveryTag, false, true, CancellationToken.None);
        }
    }

    private void Count(string outcome) => MessagingMetrics.Consumed.Add(1,
        MessagingMetrics.Tag("queue", registration.Queue), MessagingMetrics.Tag("outcome", outcome));

    /// <summary>Faux si l'événement avait déjà été traité (doublon acquitté sans effet).</summary>
    private async Task<bool> ProcessAsync(EventRoute route, IntegrationEvent integrationEvent, IChannel publish, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;

        if (registration.DbContextType is null)
        {
            // Sans base : les événements déposés par le traitement partent après lui, avant l'acquittement.
            await route.Invoke(services, integrationEvent, ct);
            foreach (var pending in services.GetRequiredService<BufferedOutbox>().Pending)
                await Publisher.PublishAsync(publish, EventTopology.Exchange, pending.RoutingKey, pending.EventId, pending.EventType,
                    pending.CorrelationId, pending.OccurredAt, EventSerializer.Serialize(pending), Activity.Current?.Id, ct);
            return true;
        }

        var db = (DbContext)services.GetRequiredService(registration.DbContextType);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        db.Set<ProcessedMessage>().Add(ProcessedMessage.Create(integrationEvent.EventId, registration.Queue, clock.GetUtcNow()));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            logger.LogInformation("Événement {EventId} déjà traité par {Queue} : ignoré", integrationEvent.EventId, registration.Queue);
            return false;
        }

        await route.Invoke(services, integrationEvent, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // Les événements écrits par ce traitement peuvent partir tout de suite.
        services.GetRequiredService<OutboxSignal>().Notify();
        return true;
    }

    private async Task DeadLetterAsync(IChannel channel, IChannel publish, BasicDeliverEventArgs delivery, byte[] body, int attempt, string error, CancellationToken ct) =>
        await RepublishAsync(channel, publish, delivery, body, Topology.DeadLetterQueue(registration.Queue), attempt, error, ct);

    /// <summary>Copie vers une file annexe, puis acquittement de l'original. Un arrêt entre les deux donne un doublon, filtré à la réception.</summary>
    private static async Task RepublishAsync(
        IChannel channel, IChannel publish, BasicDeliverEventArgs delivery, byte[] body, string queue, int attempt, string? error, CancellationToken ct)
    {
        var source = delivery.BasicProperties;
        var headers = source.Headers is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(source.Headers);
        headers[Headers.Attempt] = attempt;
        headers.TryAdd(Headers.RoutingKey, Encoding.UTF8.GetBytes(delivery.RoutingKey));
        if (error is not null) headers[Headers.Error] = error.Length > 1000 ? error[..1000] : error;

        var properties = new BasicProperties
        {
            MessageId = source.MessageId,
            Type = source.Type,
            CorrelationId = source.CorrelationId,
            ContentType = source.ContentType,
            Timestamp = source.Timestamp,
            DeliveryMode = DeliveryModes.Persistent,
            Headers = headers,
        };

        await publish.BasicPublishAsync("", queue, mandatory: true, properties, body, ct);
        await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
    }
}

/// <summary>Outbox en mémoire, pour un consommateur sans base : publié par le consommateur après le traitement.</summary>
public sealed class BufferedOutbox : IOutbox
{
    private readonly List<IntegrationEvent> _pending = [];

    public IReadOnlyList<IntegrationEvent> Pending => _pending;

    public void Enqueue(IntegrationEvent integrationEvent) => _pending.Add(integrationEvent);
}
