using System.Data.Common;
using System.Diagnostics;
using DevPulse.Contracts.Events.Common;
using DevPulse.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;

namespace DevPulse.Messaging;

/// <summary>Un événement en attente de publication, écrit dans la même transaction que le changement qu'il décrit.</summary>
public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public Guid Id { get; private set; }
    public string RoutingKey { get; private set; } = "";
    public string EventType { get; private set; } = "";
    public string Payload { get; private set; } = "";
    public string CorrelationId { get; private set; } = "";

    /// <summary>Contexte de trace W3C au moment de l'écriture : la trace continue chez les consommateurs.</summary>
    public string? TraceParent { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    public static OutboxMessage From(IntegrationEvent e) => new()
    {
        Id = e.EventId,
        RoutingKey = e.RoutingKey,
        EventType = e.EventType,
        Payload = EventSerializer.Serialize(e),
        CorrelationId = e.CorrelationId,
        TraceParent = Activity.Current?.Id,
        OccurredAt = e.OccurredAt,
        NextAttemptAt = e.OccurredAt,
    };

    public void MarkPublished(DateTimeOffset now)
    {
        PublishedAt = now;
        LastError = null;
    }

    /// <summary>Attente croissante, plafonnée à une minute : un courtier absent n'est pas martelé.</summary>
    public void MarkFailed(string error, DateTimeOffset now)
    {
        Attempts++;
        LastError = error.Length > 1000 ? error[..1000] : error;
        NextAttemptAt = now + TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Attempts)));
    }
}

/// <summary>Un événement déjà traité par un consommateur donné. La clé primaire rend un second traitement impossible.</summary>
public sealed class ProcessedMessage
{
    private ProcessedMessage() { }

    public Guid MessageId { get; private set; }
    public string Consumer { get; private set; } = "";
    public DateTimeOffset ProcessedAt { get; private set; }

    public static ProcessedMessage Create(Guid messageId, string consumer, DateTimeOffset now) =>
        new() { MessageId = messageId, Consumer = consumer, ProcessedAt = now };
}

public static class MessagingModelBuilderExtensions
{
    /// <summary>Tables d'outbox et de messages traités, à appeler dans <c>OnModelCreating</c> de chaque service.</summary>
    public static ModelBuilder AddMessagingTables(this ModelBuilder model)
    {
        model.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).ValueGeneratedNever();
            e.Property(m => m.RoutingKey).HasMaxLength(200);
            e.Property(m => m.EventType).HasMaxLength(200);
            e.Property(m => m.Payload).HasColumnType("jsonb");
            e.Property(m => m.CorrelationId).HasMaxLength(100);
            e.Property(m => m.TraceParent).HasMaxLength(100);
            e.Property(m => m.LastError).HasMaxLength(1000);
            e.HasIndex(m => m.NextAttemptAt).HasFilter("published_at IS NULL");
            e.HasIndex(m => m.PublishedAt);
        });

        model.Entity<ProcessedMessage>(e =>
        {
            e.ToTable("processed_messages");
            e.HasKey(m => new { m.MessageId, m.Consumer });
            e.Property(m => m.Consumer).HasMaxLength(100);
            e.HasIndex(m => m.ProcessedAt);
        });

        return model;
    }
}

/// <summary>Réveille le distributeur dès qu'un événement est validé, au lieu d'attendre le prochain passage.</summary>
public sealed class OutboxSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        // Un réveil en attente suffit : les suivants seraient redondants.
        if (_semaphore.CurrentCount == 0)
        {
            try { _semaphore.Release(); } catch (SemaphoreFullException) { }
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);
}

public sealed class EfOutbox<TDb>(TDb db, OutboxSignal signal) : IOutbox where TDb : DbContext
{
    private bool _subscribed;

    public void Enqueue(IntegrationEvent integrationEvent)
    {
        db.Set<OutboxMessage>().Add(OutboxMessage.From(integrationEvent));
        if (_subscribed) return;

        // Hors transaction explicite, la validation a lieu dans SaveChanges : le distributeur peut partir tout de suite.
        db.SavedChanges += (_, _) => signal.Notify();
        _subscribed = true;
    }
}

internal static class DbErrors
{
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is DbException { SqlState: "23505" };
}
