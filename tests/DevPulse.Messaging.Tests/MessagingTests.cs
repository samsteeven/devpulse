using System.Text;
using DevPulse.Contracts.Events.Monitoring;
using DevPulse.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace DevPulse.Messaging.Tests;

[Collection(BrokerCollection.Name)]
public class MessagingTests(BrokerFixture fixture)
{
    private static ServiceDownDetectedV1 Down(Guid? serviceId = null) => new()
    {
        Producer = "tests",
        OrganizationId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid().ToString("N"),
        ServiceId = serviceId ?? Guid.NewGuid(),
        ServiceName = "Payment API",
        Environment = "Production",
        Source = MonitoringSources.Probe,
        FailureCount = 2,
        Failure = "Timeout",
        DetectedAt = DateTimeOffset.UtcNow,
    };

    private async Task EnqueueAsync(params Contracts.Events.Common.IntegrationEvent[] events)
    {
        await using var db = fixture.NewDb();
        var outbox = new EfOutbox<TestDb>(db, new OutboxSignal());
        foreach (var e in events) outbox.Enqueue(e);
        await db.SaveChangesAsync();
    }

    private async Task<int> EffectsAsync(Guid serviceId)
    {
        await using var db = fixture.NewDb();
        return await db.Effects.CountAsync(e => e.ServiceId == serviceId);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition non atteinte à temps.");
            await Task.Delay(100);
        }
    }

    private async Task<uint> CountAsync(string queue)
    {
        await using var channel = await fixture.ChannelAsync();
        return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
    }

    [Fact]
    public async Task Un_evenement_ecrit_dans_l_outbox_est_publie_puis_traite_une_fois()
    {
        await fixture.PurgeAsync();
        var script = new HandlerScript();
        using var host = await fixture.StartHostAsync(script);

        var e = Down();
        await EnqueueAsync(e);

        Assert.True(await script.Done.WaitAsync(TimeSpan.FromSeconds(20)), "Événement jamais traité.");
        await WaitUntilAsync(async () => await EffectsAsync(e.ServiceId) == 1);

        await using var db = fixture.NewDb();
        var message = await db.Set<OutboxMessage>().SingleAsync(m => m.Id == e.EventId);
        Assert.NotNull(message.PublishedAt);
        Assert.True(await db.Set<ProcessedMessage>().AnyAsync(p => p.MessageId == e.EventId && p.Consumer == EventTopology.IncidentServiceState));
        await host.StopAsync();
    }

    [Fact]
    public async Task Un_doublon_du_meme_evenement_n_a_aucun_effet()
    {
        await fixture.PurgeAsync();
        var script = new HandlerScript();
        using var host = await fixture.StartHostAsync(script);

        // Le même événement publié deux fois, comme après un arrêt entre la publication et sa confirmation.
        var e = Down();
        await using (var channel = await fixture.ChannelAsync())
        {
            var body = Encoding.UTF8.GetBytes(EventSerializer.Serialize(e));
            var properties = new BasicProperties { MessageId = e.EventId.ToString(), DeliveryMode = DeliveryModes.Persistent };
            await channel.BasicPublishAsync(EventTopology.Exchange, e.RoutingKey, false, properties, body);
            await channel.BasicPublishAsync(EventTopology.Exchange, e.RoutingKey, false, properties, body);
        }

        Assert.True(await script.Done.WaitAsync(TimeSpan.FromSeconds(20)));
        await WaitUntilAsync(async () => await CountAsync(EventTopology.IncidentServiceState) == 0);
        await Task.Delay(500);

        Assert.Equal(1, await EffectsAsync(e.ServiceId));
        Assert.Equal(1, script.Calls);
        await host.StopAsync();
    }

    [Fact]
    public async Task Une_transaction_annulee_ne_publie_rien()
    {
        await fixture.PurgeAsync();
        var script = new HandlerScript();
        using var host = await fixture.StartHostAsync(script);

        var e = Down();
        await using (var db = fixture.NewDb())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            new EfOutbox<TestDb>(db, new OutboxSignal()).Enqueue(e);
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        Assert.False(await script.Done.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, script.Calls);
        await host.StopAsync();
    }

    [Fact]
    public async Task Un_echec_passager_est_repris_apres_un_delai()
    {
        await fixture.PurgeAsync();
        var script = new HandlerScript { FailuresBeforeSuccess = 2 };
        using var host = await fixture.StartHostAsync(script);

        var e = Down();
        await EnqueueAsync(e);

        Assert.True(await script.Done.WaitAsync(TimeSpan.FromSeconds(20)), "Pas de réussite après les reprises.");
        Assert.Equal(3, script.Calls);
        await WaitUntilAsync(async () => await EffectsAsync(e.ServiceId) == 1);
        Assert.Equal(0u, await CountAsync(Topology.DeadLetterQueue(EventTopology.IncidentServiceState)));
        await host.StopAsync();
    }

    [Fact]
    public async Task Un_echec_persistant_finit_en_file_morte_avec_son_erreur()
    {
        await fixture.PurgeAsync();
        var script = new HandlerScript { AlwaysFail = true };
        using var host = await fixture.StartHostAsync(script);

        var e = Down();
        await EnqueueAsync(e);

        var dlq = Topology.DeadLetterQueue(EventTopology.IncidentServiceState);
        await WaitUntilAsync(async () => await CountAsync(dlq) == 1);
        await host.StopAsync();

        Assert.Equal(3, script.Calls); // MaxAttempts = 3
        Assert.Equal(0, await EffectsAsync(e.ServiceId));

        await using var channel = await fixture.ChannelAsync();
        var dead = await channel.BasicGetAsync(dlq, autoAck: true);
        Assert.NotNull(dead);
        Assert.Equal(e.EventId.ToString(), dead.BasicProperties.MessageId);
        var error = Encoding.UTF8.GetString((byte[])dead.BasicProperties.Headers!["x-last-error"]!);
        Assert.Contains("échec voulu n° 3", error);
        // Aucun effet partiel : la trace « déjà traité » a été annulée avec le reste.
        await using var db = fixture.NewDb();
        Assert.False(await db.Set<ProcessedMessage>().AnyAsync(p => p.MessageId == e.EventId));
    }

    [Fact]
    public async Task Les_files_ne_recoivent_que_ce_qui_leur_est_lie()
    {
        await fixture.PurgeAsync();
        using var host = await fixture.StartHostAsync(new HandlerScript(), consume: false);

        await EnqueueAsync(new ServiceStatusChangedV1
        {
            Producer = "tests",
            OrganizationId = Guid.NewGuid(),
            CorrelationId = "c0ffee00",
            ServiceId = Guid.NewGuid(),
            ServiceName = "Payment API",
            Environment = "Production",
            PreviousStatus = "Operational",
            CurrentStatus = "Down",
            ChangedAt = DateTimeOffset.UtcNow,
        });

        await WaitUntilAsync(async () => await CountAsync(EventTopology.AlertingServiceStatus) == 1);
        // Un changement d'état n'ouvre pas d'incident : il n'arrive pas dans la file d'Incident.
        Assert.Equal(0u, await CountAsync(EventTopology.IncidentServiceState));
        await host.StopAsync();
    }

    [Fact]
    public async Task Le_distributeur_garde_les_evenements_tant_que_le_courtier_est_injoignable()
    {
        await fixture.PurgeAsync();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        var settings = fixture.RabbitSettings();
        settings["RabbitMq:Port"] = "1"; // personne n'écoute
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddDbContext<TestDb>(o => o.UseNpgsql(fixture.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddDevPulseMessaging("messaging-tests").AddOutbox<TestDb>();
        using var host = builder.Build();
        await host.StartAsync();

        var e = Down();
        await EnqueueAsync(e);
        await Task.Delay(1500);
        await host.StopAsync();

        await using var db = fixture.NewDb();
        Assert.Null((await db.Set<OutboxMessage>().SingleAsync(m => m.Id == e.EventId)).PublishedAt);
    }
}

public class TopologyTests
{
    [Theory]
    [InlineData("monitoring.service.*", "monitoring.service.down-detected.v1", false)]
    [InlineData("monitoring.service.*.v1", "monitoring.service.down-detected.v1", true)]
    [InlineData("monitoring.service.#", "monitoring.service.down-detected.v1", true)]
    [InlineData("monitoring.#", "monitoring", true)]
    [InlineData("incident.incident.*.v1", "incident.incident.created.v1", true)]
    [InlineData("incident.incident.*.v1", "incident.incident.created.v2", false)]
    public void La_semantique_topic_est_respectee(string binding, string key, bool expected) =>
        Assert.Equal(expected, Topology.Matches(binding, key));

    [Fact]
    public void Un_traitement_pour_un_evenement_non_lie_a_la_file_empeche_le_demarrage()
    {
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => services.AddDevPulseMessaging("tests")
            .AddConsumer<TestDb>(EventTopology.AlertingServiceStatus, c => c.Handle<ServiceDownDetectedV1, DownHandler>()));
        Assert.Contains("n'est pas liée", error.Message);
    }

    [Fact]
    public void La_cle_de_routage_se_lit_sans_construire_l_evenement() =>
        Assert.Equal("monitoring.service.recovered.v1", EventSerializer.RoutingKeyOf(typeof(ServiceRecoveredV1)));
}
