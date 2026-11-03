using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using DevPulse.Contracts.Events.Monitoring;
using DevPulse.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace DevPulse.Messaging.Tests;

/// <summary>Écoute les mesures DevPulse émises pendant un test.</summary>
public sealed class Measurements : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(string Name, double Value, Dictionary<string, object?> Tags)> _values = new();

    public Measurements()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name.StartsWith("DevPulse.", StringComparison.Ordinal)) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
        _listener.Start();
    }

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        _values.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));

    /// <summary>Relit les jauges (elles ne sont mesurées qu'à la demande).</summary>
    public void Observe() => _listener.RecordObservableInstruments();

    public double Sum(string name, params (string Key, object Value)[] tags) => _values
        .Where(m => m.Name == name && tags.All(t => Equals(m.Tags.GetValueOrDefault(t.Key)?.ToString(), t.Value.ToString())))
        .Sum(m => m.Value);

    public double? Last(string name, params (string Key, object Value)[] tags) => _values
        .Where(m => m.Name == name && tags.All(t => Equals(m.Tags.GetValueOrDefault(t.Key)?.ToString(), t.Value.ToString())))
        .Select(m => (double?)m.Value).LastOrDefault();

    public void Dispose() => _listener.Dispose();
}

[Collection(BrokerCollection.Name)]
public class MetricsTests(BrokerFixture fixture)
{
    private const string Queue = EventTopology.IncidentServiceState;

    private static ServiceDownDetectedV1 Down() => new()
    {
        Producer = "tests", OrganizationId = Guid.NewGuid(), CorrelationId = "c0ffee00", ServiceId = Guid.NewGuid(),
        ServiceName = "Payment API", Environment = "Production", Source = MonitoringSources.Probe,
        FailureCount = 2, Failure = "Timeout", DetectedAt = DateTimeOffset.UtcNow,
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Mesure non observée à temps.");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Publication_traitement_et_doublon_sont_comptes()
    {
        await fixture.PurgeAsync();
        using var measurements = new Measurements();
        var script = new HandlerScript();
        using var host = await fixture.StartHostAsync(script);

        var e = Down();
        await using (var db = fixture.NewDb())
        {
            new EfOutbox<TestDb>(db, new OutboxSignal()).Enqueue(e);
            await db.SaveChangesAsync();
        }
        await WaitUntilAsync(() => measurements.Sum("devpulse.messaging.consumed", ("queue", Queue), ("outcome", "processed")) == 1);

        // Le même événement, renvoyé à la main : compté comme doublon, sans second traitement.
        await using (var channel = await fixture.ChannelAsync())
            await channel.BasicPublishAsync(EventTopology.Exchange, e.RoutingKey, false,
                new BasicProperties { MessageId = e.EventId.ToString() }, Encoding.UTF8.GetBytes(EventSerializer.Serialize(e)));
        await WaitUntilAsync(() => measurements.Sum("devpulse.messaging.consumed", ("queue", Queue), ("outcome", "duplicate")) == 1);

        Assert.Equal(1, measurements.Sum("devpulse.messaging.published", ("routing_key", e.RoutingKey)));
        Assert.True(measurements.Sum("devpulse.messaging.processing.duration") > 0);
        await host.StopAsync();
    }

    [Fact]
    public async Task Les_reprises_et_la_file_morte_sont_comptees()
    {
        await fixture.PurgeAsync();
        using var measurements = new Measurements();
        using var host = await fixture.StartHostAsync(new HandlerScript { AlwaysFail = true });

        await using (var db = fixture.NewDb())
        {
            new EfOutbox<TestDb>(db, new OutboxSignal()).Enqueue(Down());
            await db.SaveChangesAsync();
        }

        await WaitUntilAsync(() => measurements.Sum("devpulse.messaging.consumed", ("queue", Queue), ("outcome", "dead_lettered")) == 1);
        Assert.Equal(2, measurements.Sum("devpulse.messaging.consumed", ("queue", Queue), ("outcome", "retried"))); // MaxAttempts = 3
        await host.StopAsync();
    }

    [Fact]
    public async Task La_jauge_de_l_outbox_grandit_quand_le_courtier_est_injoignable()
    {
        await fixture.PurgeAsync();
        await using (var db = fixture.NewDb())
        {
            new EfOutbox<TestDb>(db, new OutboxSignal()).Enqueue(Down() with { OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-3) });
            await db.SaveChangesAsync();
        }

        using var measurements = new Measurements();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        var settings = fixture.RabbitSettings();
        settings["RabbitMq:Port"] = "1"; // personne n'écoute
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddDbContext<TestDb>(o => o.UseNpgsql(fixture.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddDevPulseMessaging("messaging-tests").AddOutbox<TestDb>();
        using var host = builder.Build();
        await host.StartAsync();

        // Les jauges sont un état du processus : un test précédent a pu y laisser sa dernière valeur. On attend donc
        // celle de cet événement-ci, vieux de trois minutes, plutôt que la première valeur venue.
        await WaitUntilAsync(() =>
        {
            measurements.Observe();
            return measurements.Last("devpulse.outbox.oldest.age", ("store", nameof(TestDb))) is >= 170 and <= 600;
        });
        Assert.Equal(1, measurements.Last("devpulse.outbox.pending", ("store", nameof(TestDb))));
        await host.StopAsync();
    }
}
