using System.Diagnostics;
using System.Text;
using DevPulse.Contracts.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace DevPulse.Messaging;

public static class MessagingTelemetry
{
    public const string SourceName = "DevPulse.Messaging";
    internal static readonly ActivitySource Source = new(SourceName);

    internal static ActivityContext ParentFrom(string? traceParent) =>
        traceParent is not null && ActivityContext.TryParse(traceParent, null, out var context) ? context : default;
}

internal static class Headers
{
    public const string Attempt = "x-attempt";
    public const string RoutingKey = "x-routing-key";
    public const string Error = "x-last-error";
    public const string TraceParent = "traceparent";

    public static string? ReadString(IDictionary<string, object?>? headers, string name) =>
        headers is not null && headers.TryGetValue(name, out var value)
            ? value switch { byte[] bytes => Encoding.UTF8.GetString(bytes), string text => text, _ => value?.ToString() }
            : null;

    public static int ReadInt(IDictionary<string, object?>? headers, string name, int fallback) =>
        headers is not null && headers.TryGetValue(name, out var value)
            ? value switch { int i => i, long l => (int)l, byte[] b when int.TryParse(Encoding.UTF8.GetString(b), out var p) => p, _ => fallback }
            : fallback;
}

/// <summary>Une connexion par processus, partagée par tous les canaux. La reconnexion est automatique une fois la première établie.</summary>
public sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options, ServiceName service, ILogger<RabbitMqConnection> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is not null) return _connection;

        await _lock.WaitAsync(ct);
        try
        {
            if (_connection is not null) return _connection;

            var o = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = o.Host,
                Port = o.Port,
                UserName = o.User,
                Password = o.Password,
                VirtualHost = o.VirtualHost,
                ClientProvidedName = service.Value,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
                Ssl = new SslOption { Enabled = o.UseTls, ServerName = o.Host },
            };

            // Le courtier peut démarrer après le service : on attend sans planter, l'outbox garde les événements.
            var delay = TimeSpan.FromSeconds(1);
            while (true)
            {
                try
                {
                    _connection = await factory.CreateConnectionAsync(ct);
                    logger.LogInformation("Connecté à RabbitMQ ({Host}:{Port})", o.Host, o.Port);
                    return _connection;
                }
                catch (BrokerUnreachableException e)
                {
                    logger.LogWarning(e, "RabbitMQ injoignable ; nouvel essai dans {Delay} s", delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IChannel> CreatePublishChannelAsync(CancellationToken ct)
    {
        var connection = await GetAsync(ct);
        // Confirmations suivies : BasicPublishAsync ne rend la main qu'une fois le message accepté par le courtier.
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await Topology.DeclareAsync(channel, options.Value, ct);
        return channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }
}

/// <summary>Nom du service, utilisé comme nom de connexion et comme champ <c>producer</c> des événements.</summary>
public sealed record ServiceName(string Value);

/// <summary>
/// Chaque file a une file de reprise (durée de vie, puis retour dans la file principale) et une file morte.
/// Tout est déclaré par chaque service au démarrage : un événement publié avant le démarrage de son consommateur
/// attend dans sa file au lieu d'être perdu.
/// </summary>
public static class Topology
{
    public static string RetryQueue(string queue) => $"{queue}.retry";
    public static string DeadLetterQueue(string queue) => $"{queue}.dlq";

    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions options, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(EventTopology.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);

        var quorum = new Dictionary<string, object?> { ["x-queue-type"] = "quorum" };
        foreach (var (queue, bindings) in EventTopology.Bindings)
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments: quorum, cancellationToken: ct);
            await channel.QueueDeclareAsync(RetryQueue(queue), durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-message-ttl"] = options.RetryDelaySeconds * 1000,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = queue,
            }, cancellationToken: ct);
            await channel.QueueDeclareAsync(DeadLetterQueue(queue), durable: true, exclusive: false, autoDelete: false, arguments: quorum, cancellationToken: ct);

            foreach (var binding in bindings)
                await channel.QueueBindAsync(queue, EventTopology.Exchange, binding, cancellationToken: ct);
        }
    }

    /// <summary>Sémantique des échanges « topic » : « * » remplace exactement un mot, « # » zéro ou plusieurs.</summary>
    public static bool Matches(string binding, string routingKey) =>
        Match(binding.Split('.'), 0, routingKey.Split('.'), 0);

    private static bool Match(string[] pattern, int p, string[] words, int w)
    {
        if (p == pattern.Length) return w == words.Length;
        if (pattern[p] == "#")
        {
            for (var next = w; next <= words.Length; next++)
                if (Match(pattern, p + 1, words, next)) return true;
            return false;
        }
        return w < words.Length && (pattern[p] == "*" || pattern[p] == words[w]) && Match(pattern, p + 1, words, w + 1);
    }
}

internal static class Publisher
{
    public static async Task PublishAsync(
        IChannel channel, string exchange, string routingKey, Guid messageId, string eventType, string correlationId,
        DateTimeOffset occurredAt, string payload, string? traceParent, CancellationToken ct)
    {
        using var activity = MessagingTelemetry.Source.StartActivity(
            $"{routingKey} publish", ActivityKind.Producer, MessagingTelemetry.ParentFrom(traceParent));
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", exchange);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", routingKey);
        activity?.SetTag("messaging.message.id", messageId.ToString());

        var properties = new BasicProperties
        {
            MessageId = messageId.ToString(),
            Type = eventType,
            CorrelationId = correlationId,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(occurredAt.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>
            {
                [Headers.RoutingKey] = routingKey,
                [Headers.TraceParent] = activity?.Id ?? traceParent,
            },
        };

        // « mandatory » : un événement qu'aucune file ne reçoit revient en erreur au lieu de disparaître.
        await channel.BasicPublishAsync(exchange, routingKey, mandatory: true, properties, Encoding.UTF8.GetBytes(payload), ct);
    }
}
