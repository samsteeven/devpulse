using DevPulse.Contracts.Events.Monitoring;
using DevPulse.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace DevPulse.Messaging.Tests;

/// <summary>Une base de test minimale : les tables de messagerie et une table d'effets visibles.</summary>
public sealed class TestDb(DbContextOptions<TestDb> options) : DbContext(options)
{
    public DbSet<Effect> Effects => Set<Effect>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.AddMessagingTables();
        model.Entity<Effect>(e => e.ToTable("effects"));
    }
}

public sealed class Effect
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ServiceId { get; set; }
    public string Kind { get; set; } = "";
}

/// <summary>Commande le comportement du traitement de test d'un essai à l'autre.</summary>
public sealed class HandlerScript
{
    public int FailuresBeforeSuccess { get; set; }
    public bool AlwaysFail { get; set; }
    public int Calls;
    public readonly SemaphoreSlim Done = new(0);
}

public sealed class DownHandler(TestDb db, HandlerScript script) : IIntegrationEventHandler<ServiceDownDetectedV1>
{
    public Task HandleAsync(ServiceDownDetectedV1 e, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref script.Calls);
        if (script.AlwaysFail || call <= script.FailuresBeforeSuccess)
            throw new InvalidOperationException($"échec voulu n° {call}");

        db.Effects.Add(new Effect { ServiceId = e.ServiceId, Kind = "down" });
        script.Done.Release();
        return Task.CompletedTask;
    }
}

public sealed class BrokerFixture : IAsyncLifetime
{
    // Données en mémoire : rien n'est écrit dans le disque virtuel de Docker.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.11-alpine")
        .WithTmpfsMount("/var/lib/postgresql/data").Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.3.7-management-alpine")
        .WithUsername("devpulse").WithPassword("devpulse")
        .WithTmpfsMount("/var/lib/rabbitmq").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        await using var db = NewDb();
        await db.Database.EnsureCreatedAsync();

        // Files déclarées d'avance, avec les mêmes arguments que les services : un test peut publier dès le départ.
        await using var channel = await ChannelAsync();
        await Topology.DeclareAsync(channel, new RabbitMqOptions { RetryDelaySeconds = 1 }, default);
    }

    public TestDb NewDb() => new(new DbContextOptionsBuilder<TestDb>()
        .UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention().Options);

    public Dictionary<string, string?> RabbitSettings(int retryDelaySeconds = 1, int maxAttempts = 3) => new()
    {
        ["RabbitMq:Host"] = _rabbit.Hostname,
        ["RabbitMq:Port"] = _rabbit.GetMappedPublicPort(5672).ToString(),
        ["RabbitMq:User"] = "devpulse",
        ["RabbitMq:Password"] = "devpulse",
        ["RabbitMq:RetryDelaySeconds"] = retryDelaySeconds.ToString(),
        ["RabbitMq:MaxAttempts"] = maxAttempts.ToString(),
        ["RabbitMq:OutboxPollMilliseconds"] = "200",
    };

    /// <summary>Un service complet : outbox et consommateur d'<c>incident.service-state</c>, sur la base de test.</summary>
    public async Task<IHost> StartHostAsync(HandlerScript script, bool consume = true)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(RabbitSettings());
        builder.Services.AddDbContext<TestDb>(o => o.UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddSingleton(script);

        var messaging = builder.Services.AddDevPulseMessaging("messaging-tests").AddOutbox<TestDb>();
        if (consume)
            messaging.AddConsumer<TestDb>(EventTopology.IncidentServiceState, c => c.Handle<ServiceDownDetectedV1, DownHandler>());

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    public async Task<IChannel> ChannelAsync()
    {
        var factory = new ConnectionFactory
        {
            HostName = _rabbit.Hostname,
            Port = _rabbit.GetMappedPublicPort(5672),
            UserName = "devpulse",
            Password = "devpulse",
        };
        var connection = await factory.CreateConnectionAsync();
        return await connection.CreateChannelAsync();
    }

    /// <summary>Vide les files entre deux tests : elles sont partagées dans la collection.</summary>
    public async Task PurgeAsync()
    {
        await using var channel = await ChannelAsync();
        foreach (var queue in EventTopology.Bindings.Keys)
        {
            foreach (var name in new[] { queue, Topology.RetryQueue(queue), Topology.DeadLetterQueue(queue) })
            {
                try { await channel.QueuePurgeAsync(name); } catch (RabbitMQ.Client.Exceptions.OperationInterruptedException) { return; }
            }
        }

        await using var db = NewDb();
        await db.Effects.ExecuteDeleteAsync();
        await db.Set<OutboxMessage>().ExecuteDeleteAsync();
        await db.Set<ProcessedMessage>().ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        await _rabbit.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public class BrokerCollection : ICollectionFixture<BrokerFixture>
{
    public const string Name = "broker";
}
