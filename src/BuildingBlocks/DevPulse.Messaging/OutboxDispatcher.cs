using DevPulse.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using RabbitMQ.Client;

namespace DevPulse.Messaging;

/// <summary>
/// Publie les événements de l'outbox, dans l'ordre d'écriture, avec confirmation du courtier. Plusieurs instances
/// peuvent tourner sur la même base (API et worker de Monitoring) : <c>SKIP LOCKED</c> leur donne des lots distincts.
/// Un message n'est marqué publié qu'après confirmation ; un arrêt entre les deux le republie (au moins une fois).
/// </summary>
public sealed class OutboxDispatcher<TDb>(
    IServiceScopeFactory scopes, RabbitMqConnection connection, OutboxSignal signal,
    IOptions<RabbitMqOptions> options, TimeProvider clock, ILogger<OutboxDispatcher<TDb>> logger) : BackgroundService
    where TDb : DbContext
{
    private DateTimeOffset _nextCleanup;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled) return;

        IChannel? channel = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var full = false;
                try
                {
                    channel ??= await connection.CreatePublishChannelAsync(stoppingToken);
                    full = await DispatchBatchAsync(channel, settings, stoppingToken) == settings.OutboxBatchSize;
                    await CleanupAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Publication de l'outbox interrompue ; nouvel essai sous peu");
                    if (channel is { IsOpen: false }) { await channel.DisposeAsync(); channel = null; }
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }

                // Lot plein : il en reste sans doute, on enchaîne. Sinon on attend un réveil ou le passage suivant.
                if (!full) await signal.WaitAsync(TimeSpan.FromMilliseconds(settings.OutboxPollMilliseconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (channel is not null) await channel.DisposeAsync();
        }
    }

    private async Task<int> DispatchBatchAsync(IChannel channel, RabbitMqOptions settings, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDb>();
        var now = clock.GetUtcNow();

        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction;
        List<OutboxMessage> due;

        // L'interrogation périodique n'apparaît pas dans les traces : seules les publications réelles y figurent.
        using (SuppressInstrumentationScope.Begin())
        {
            transaction = await db.Database.BeginTransactionAsync(ct);
            due = await db.Set<OutboxMessage>().FromSqlInterpolated($"""
                SELECT * FROM outbox_messages
                WHERE published_at IS NULL AND next_attempt_at <= {now}
                ORDER BY occurred_at
                LIMIT {settings.OutboxBatchSize}
                FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);

            if (due.Count == 0)
            {
                await transaction.DisposeAsync();
                return 0;
            }
        }

        await using (transaction)
        {
            var published = 0;
            foreach (var message in due)
            {
                try
                {
                    await Publisher.PublishAsync(channel, EventTopology.Exchange, message.RoutingKey, message.Id, message.EventType,
                        message.CorrelationId, message.OccurredAt, message.Payload, message.TraceParent, ct);
                    message.MarkPublished(clock.GetUtcNow());
                    published++;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    message.MarkFailed($"{e.GetType().Name}: {e.Message}", clock.GetUtcNow());
                    logger.LogWarning(e, "Événement {EventId} ({RoutingKey}) non publié, tentative {Attempts}", message.Id, message.RoutingKey, message.Attempts);
                    if (!channel.IsOpen) break; // les suivants échoueraient de la même façon
                }
            }

            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            if (published > 0) logger.LogDebug("{Count} événement(s) publié(s)", published);
            return due.Count;
        }
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (now < _nextCleanup) return;
        _nextCleanup = now.AddHours(1);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDb>();
        var published = await db.Set<OutboxMessage>().Where(m => m.PublishedAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);
        var processed = await db.Set<ProcessedMessage>().Where(m => m.ProcessedAt < now.AddDays(-14)).ExecuteDeleteAsync(ct);
        if (published + processed > 0)
            logger.LogInformation("Nettoyage messagerie : {Published} événement(s) publié(s), {Processed} trace(s) de traitement", published, processed);
    }
}
