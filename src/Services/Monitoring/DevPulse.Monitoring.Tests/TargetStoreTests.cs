using System.Net;
using System.Net.Http.Json;
using DevPulse.Contracts.Internal;
using DevPulse.Messaging;
using DevPulse.Monitoring.Application;
using DevPulse.Monitoring.Domain;
using DevPulse.Monitoring.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DevPulse.Monitoring.Tests;

[Collection(MonitoringCollection.Name)]
public class TargetStoreTests(MonitoringFixture fixture)
{
    private static ActiveServiceDto Service(Guid? organization = null, string url = "https://api.example.com/health", int interval = 60) =>
        new(Guid.NewGuid(), organization ?? Guid.NewGuid(), "Payment API", "Production", url, interval, 5);

    private static TargetStore Store(MonitoringDb db) => new(db, new EfOutbox<MonitoringDb>(db, new OutboxSignal()));

    /// <summary>Isole chaque test : les tables sont partagées dans la collection.</summary>
    private async Task ResetAsync()
    {
        await using var db = fixture.NewDb();
        await db.Checks.ExecuteDeleteAsync();
        await db.Targets.ExecuteDeleteAsync();
        await db.Set<OutboxMessage>().ExecuteDeleteAsync();
    }

    private async Task<List<string>> OutboxAsync()
    {
        await using var db = fixture.NewDb();
        return await db.Set<OutboxMessage>().OrderBy(m => m.OccurredAt).Select(m => m.RoutingKey).ToListAsync();
    }

    private async Task SyncAsync(IReadOnlyList<ActiveServiceDto> active, DateTimeOffset now)
    {
        await using var db = fixture.NewDb();
        await Store(db).SynchronizeAsync(active, now, default);
    }

    private async Task<IReadOnlyList<CheckAssignment>> ClaimAsync(int max, DateTimeOffset now)
    {
        await using var db = fixture.NewDb();
        return await Store(db).ClaimDueAsync(max, now, default);
    }

    private static ProbeResult Result(HealthStatus status, DateTimeOffset at, int ms = 120, int? http = 200) =>
        new(status, http, ms, status == HealthStatus.Down ? ProbeFailure.UnexpectedStatus : ProbeFailure.None, at);

    [Fact]
    public async Task Une_cible_nouvelle_est_due_tout_de_suite_puis_seulement_a_l_intervalle_suivant()
    {
        await ResetAsync();
        var now = DateTimeOffset.UtcNow;
        await SyncAsync([Service(interval: 60)], now);

        Assert.Single(await ClaimAsync(10, now));
        Assert.Empty(await ClaimAsync(10, now));                  // déjà réservée
        Assert.Empty(await ClaimAsync(10, now.AddSeconds(59)));   // pas encore due
        Assert.Single(await ClaimAsync(10, now.AddSeconds(60)));
    }

    [Fact]
    public async Task Deux_workers_en_parallele_ne_prennent_jamais_la_meme_cible()
    {
        await ResetAsync();
        var now = DateTimeOffset.UtcNow;
        await SyncAsync(Enumerable.Range(0, 200).Select(_ => Service()).ToList(), now);

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ClaimAsync(50, now)));
        var ids = claims.SelectMany(c => c).Select(c => c.ServiceId).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.True(ids.Count > 0);
    }

    [Fact]
    public async Task La_reservation_respecte_la_limite_demandee()
    {
        await ResetAsync();
        var now = DateTimeOffset.UtcNow;
        await SyncAsync(Enumerable.Range(0, 5).Select(_ => Service()).ToList(), now);

        Assert.Equal(2, (await ClaimAsync(2, now)).Count);
        Assert.Equal(3, (await ClaimAsync(10, now)).Count);
    }

    [Fact]
    public async Task Une_cible_retiree_de_Registry_n_est_plus_verifiee_et_revient_si_elle_est_reactivee()
    {
        await ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var service = Service();
        await SyncAsync([service], now);
        await SyncAsync([], now);
        Assert.Empty(await ClaimAsync(10, now));

        await SyncAsync([service], now);
        Assert.Single(await ClaimAsync(10, now));
    }

    [Fact]
    public async Task Changer_l_url_efface_l_etat_passe()
    {
        await ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var service = Service();
        await SyncAsync([service], now);
        await ClaimAsync(10, now);
        await using (var db = fixture.NewDb())
            await Store(db).RecordAsync(service.ServiceId, Result(HealthStatus.Down, now, http: 503), default);

        await SyncAsync([service with { TargetUrl = "https://new.example.com/health" }], now);

        await using var check = fixture.NewDb();
        var target = await check.Targets.SingleAsync(t => t.ServiceId == service.ServiceId);
        Assert.Equal(HealthStatus.Unknown, target.CurrentStatus);
        Assert.Equal(0, target.ConsecutiveFailures);
        Assert.Single(await ClaimAsync(10, now));
    }

    [Fact]
    public async Task Les_echecs_consecutifs_s_accumulent_et_retombent_au_premier_succes()
    {
        await ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var service = Service();
        await SyncAsync([service], now);

        async Task<int> RecordAsync(HealthStatus status)
        {
            await using var db = fixture.NewDb();
            await Store(db).RecordAsync(service.ServiceId, Result(status, now), default);
            return (await db.Targets.AsNoTracking().SingleAsync(t => t.ServiceId == service.ServiceId)).ConsecutiveFailures;
        }

        Assert.Equal(1, await RecordAsync(HealthStatus.Down));
        Assert.Equal(2, await RecordAsync(HealthStatus.Down));
        Assert.Equal(3, await RecordAsync(HealthStatus.Down));
        Assert.Equal(0, await RecordAsync(HealthStatus.Degraded));
    }

    [Fact]
    public async Task Une_panne_n_est_annoncee_qu_au_deuxieme_echec_et_sa_reprise_une_seule_fois()
    {
        await ResetAsync();
        var start = DateTimeOffset.UtcNow;
        var service = Service();
        await SyncAsync([service], start);

        async Task<List<string>> RecordAsync(HealthStatus status, int minute)
        {
            var before = (await OutboxAsync()).Count;
            await using (var db = fixture.NewDb())
                await Store(db).RecordAsync(service.ServiceId, Result(status, start.AddMinutes(minute)), default);
            return (await OutboxAsync()).Skip(before).ToList();
        }

        Assert.Equal(["monitoring.service.status-changed.v1"], await RecordAsync(HealthStatus.Operational, 0));
        // Un seul échec : l'état affiché change, aucune panne n'est annoncée.
        Assert.Equal(["monitoring.service.status-changed.v1"], await RecordAsync(HealthStatus.Down, 1));
        Assert.Equal(["monitoring.service.down-detected.v1"], await RecordAsync(HealthStatus.Down, 2));
        Assert.Empty(await RecordAsync(HealthStatus.Down, 3));
        // Lent mais répond : la panne est terminée.
        Assert.Equal(["monitoring.service.status-changed.v1", "monitoring.service.recovered.v1"], await RecordAsync(HealthStatus.Degraded, 4));
        Assert.Equal(["monitoring.service.status-changed.v1"], await RecordAsync(HealthStatus.Operational, 5));

        await using var check = fixture.NewDb();
        var events = await check.Set<OutboxMessage>().ToListAsync();
        var down = System.Text.Json.JsonDocument.Parse(events.Single(e => e.EventType == "monitoring.service.down-detected").Payload).RootElement;
        Assert.Equal(2, down.GetProperty("failureCount").GetInt32());
        Assert.Equal(service.OrganizationId, down.GetProperty("organizationId").GetGuid());
        Assert.Equal("Payment API", down.GetProperty("serviceName").GetString());

        // La panne date du premier échec, pas de sa confirmation.
        var recovered = System.Text.Json.JsonDocument.Parse(events.Single(e => e.EventType == "monitoring.service.recovered").Payload).RootElement;
        Assert.Equal(start.AddMinutes(1), recovered.GetProperty("downSince").GetDateTimeOffset(), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Un_echec_isole_ne_produit_jamais_de_reprise()
    {
        await ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var service = Service();
        await SyncAsync([service], now);

        foreach (var status in new[] { HealthStatus.Down, HealthStatus.Operational, HealthStatus.Down, HealthStatus.Operational })
        {
            await using var db = fixture.NewDb();
            await Store(db).RecordAsync(service.ServiceId, Result(status, now), default);
        }

        Assert.DoesNotContain(await OutboxAsync(), key => key.Contains("down-detected") || key.Contains("recovered"));
    }

    [Fact]
    public async Task La_purge_ne_retire_que_l_historique_trop_ancien()
    {
        await ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var service = Service();
        await SyncAsync([service], now);
        await using (var db = fixture.NewDb())
        {
            var store = Store(db);
            await store.RecordAsync(service.ServiceId, Result(HealthStatus.Operational, now.AddDays(-8)), default);
            await store.RecordAsync(service.ServiceId, Result(HealthStatus.Operational, now), default);

            Assert.Equal(1, await store.PurgeOlderThanAsync(now.AddDays(-7), default));
            Assert.Equal(1, await db.Checks.CountAsync());
        }
    }

    [Fact]
    public async Task L_api_n_expose_que_les_donnees_de_l_organisation_du_jeton()
    {
        await ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var service = Service(alice);
        await SyncAsync([service, Service(bob)], now);
        await using (var db = fixture.NewDb())
            await Store(db).RecordAsync(service.ServiceId, Result(HealthStatus.Down, now, ms: 40, http: 503), default);

        var aliceClient = fixture.As(alice);
        var overview = await aliceClient.GetFromJsonAsync<OverviewResponse>("/api/dashboard/overview");
        Assert.Equal(1, overview!.Total);
        Assert.Equal(1, overview.Down);
        var line = Assert.Single(overview.Services);
        Assert.Equal(service.ServiceId, line.ServiceId);
        Assert.Equal("UnexpectedStatus", line.LastFailure);

        var bobClient = fixture.As(bob);
        Assert.Equal(HttpStatusCode.NotFound, (await bobClient.GetAsync($"/api/services/{service.ServiceId}/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bobClient.GetAsync($"/api/services/{service.ServiceId}/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Anonymous().GetAsync("/api/dashboard/overview")).StatusCode);
    }

    [Fact]
    public async Task Les_metriques_calculent_la_disponibilite_et_le_p95_sur_les_seules_reponses()
    {
        await ResetAsync();
        var now = DateTimeOffset.UtcNow;
        var organization = Guid.NewGuid();
        var service = Service(organization);
        await SyncAsync([service], now);
        await using (var db = fixture.NewDb())
        {
            var store = Store(db);
            for (var i = 1; i <= 19; i++)
                await store.RecordAsync(service.ServiceId, Result(HealthStatus.Operational, now.AddMinutes(-i), ms: i * 10), default);
            await store.RecordAsync(service.ServiceId, Result(HealthStatus.Down, now.AddMinutes(-30), ms: 5000, http: null), default);
        }

        var metrics = await fixture.As(organization).GetFromJsonAsync<ServiceMetricsResponse>($"/api/services/{service.ServiceId}/metrics?hours=1");

        Assert.Equal(20, metrics!.Checks);
        Assert.Equal(95d, metrics.UptimePercent);
        Assert.Equal(100, metrics.AverageResponseTimeMs);
        Assert.Equal(190, metrics.P95ResponseTimeMs); // l'échec à 5 000 ms ne compte pas
        Assert.Equal(20, metrics.Series.Sum(p => p.Checks));
        Assert.Equal(1, metrics.Series.Sum(p => p.Failures));
    }
}
