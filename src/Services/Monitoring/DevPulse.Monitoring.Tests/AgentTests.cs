using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DevPulse.Messaging;
using DevPulse.Monitoring.Application;
using DevPulse.Monitoring.Domain;
using DevPulse.Monitoring.Infrastructure;
using DevPulse.Security.Authentication;
using Microsoft.EntityFrameworkCore;

namespace DevPulse.Monitoring.Tests;

public class AgentDomainTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private static AgentReport Report(DateTimeOffset at, int interval = 30) => new("srv-01", "Linux", "1.0.0", interval, 12.5, 40, 70, 3600, at);

    [Fact]
    public void La_cle_a_la_forme_prevue_et_seule_la_bonne_est_acceptee()
    {
        var (agent, key) = Agent.Register(Guid.NewGuid(), "Serveur paie", "Production", T0);
        var parsed = Agent.ParseKey(key);

        Assert.NotNull(parsed);
        Assert.Equal(agent.Id, parsed.Value.AgentId);
        Assert.True(agent.Accepts(parsed.Value.Secret));
        Assert.False(agent.Accepts(new string('0', 48)));
        Assert.Null(Agent.ParseKey("dpa_pas-un-guid_abc"));

        agent.Revoke(T0);
        Assert.False(agent.Accepts(parsed.Value.Secret));
    }

    [Fact]
    public void Le_silence_est_annonce_une_fois_apres_trois_battements_manques_puis_la_reprise_le_termine()
    {
        var (agent, _) = Agent.Register(Guid.NewGuid(), "Serveur paie", "Production", T0);
        agent.Report(Report(T0));

        Assert.False(agent.CheckSilence(T0.AddSeconds(89), out _));
        Assert.True(agent.CheckSilence(T0.AddSeconds(91), out var silent));
        Assert.True(silent.OutageStarted);
        Assert.Equal(HealthStatus.Down, agent.Status);
        Assert.False(agent.CheckSilence(T0.AddSeconds(200), out _));

        var back = agent.Report(Report(T0.AddSeconds(300)));
        Assert.Equal(T0, back.EndedOutageSince);
        Assert.Equal(HealthStatus.Operational, agent.Status);
    }

    [Fact]
    public void Un_seuil_de_silence_ne_descend_jamais_sous_une_minute()
    {
        var (agent, _) = Agent.Register(Guid.NewGuid(), "a", "Staging", T0);
        agent.Report(Report(T0, interval: 10));
        Assert.Equal(TimeSpan.FromSeconds(60), agent.SilenceThreshold);
    }

    [Theory]
    [InlineData(120.0, 30)]
    [InlineData(-1.0, 30)]
    [InlineData(50.0, 5)]
    public void Des_mesures_hors_bornes_sont_refusees(double cpu, int interval)
    {
        var (agent, _) = Agent.Register(Guid.NewGuid(), "a", "Staging", T0);
        Assert.Throws<AgentException>(() => agent.Report(new AgentReport("h", "Linux", "1", interval, cpu, 10, 10, 1, T0)));
    }
}

[Collection(MonitoringCollection.Name)]
public class AgentApiTests(MonitoringFixture fixture)
{
    private static object Beat(int interval = 30) => new
    {
        hostname = "srv-paie-01", operatingSystem = "Linux 6.8", agentVersion = "1.0.0", intervalSeconds = interval,
        cpuPercent = 23.4, memoryPercent = 61.2, diskPercent = 48.0, uptimeSeconds = 86400,
    };

    private HttpClient AgentClient(string key)
    {
        var client = fixture.Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Agent", key);
        return client;
    }

    private async Task<AgentCreated> RegisterAsync(Guid organizationId)
    {
        var response = await fixture.As(organizationId, OrganizationRoles.DevOps).PostAsJsonAsync("/api/agents", new { name = "Serveur paie", environment = "Production" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AgentCreated>())!;
    }

    [Fact]
    public async Task Un_agent_enregistre_envoie_ses_mesures_et_apparait_en_ligne()
    {
        var organization = Guid.NewGuid();
        var created = await RegisterAsync(organization);
        Assert.StartsWith("dpa_", created.Key);
        Assert.Equal("Unknown", created.Agent.Status);

        Assert.Equal(HttpStatusCode.Accepted, (await AgentClient(created.Key).PostAsJsonAsync("/api/agents/ingest", Beat())).StatusCode);

        var agent = Assert.Single((await fixture.As(organization, OrganizationRoles.Viewer).GetFromJsonAsync<List<AgentResponse>>("/api/agents"))!);
        Assert.Equal("Operational", agent.Status);
        Assert.Equal("srv-paie-01", agent.Hostname);
        Assert.Equal(23.4, agent.CpuPercent);

        var samples = await fixture.As(organization, OrganizationRoles.Viewer).GetFromJsonAsync<List<AgentSamplePoint>>($"/api/agents/{agent.Id}/samples");
        Assert.Single(samples!);
    }

    [Fact]
    public async Task Une_cle_fausse_ou_revoquee_est_refusee_sans_distinction()
    {
        var organization = Guid.NewGuid();
        var created = await RegisterAsync(organization);
        var forged = created.Key[..^4] + "0000";

        Assert.Equal(HttpStatusCode.Unauthorized, (await AgentClient(forged).PostAsJsonAsync("/api/agents/ingest", Beat())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Anonymous().PostAsJsonAsync("/api/agents/ingest", Beat())).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await fixture.As(organization, OrganizationRoles.Admin).DeleteAsync($"/api/agents/{created.Agent.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AgentClient(created.Key).PostAsJsonAsync("/api/agents/ingest", Beat())).StatusCode);
    }

    [Fact]
    public async Task Une_autre_organisation_ne_voit_ni_ne_revoque_l_agent()
    {
        var created = await RegisterAsync(Guid.NewGuid());
        var stranger = fixture.As(Guid.NewGuid(), OrganizationRoles.Owner);

        Assert.Empty((await stranger.GetFromJsonAsync<List<AgentResponse>>("/api/agents"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/agents/{created.Agent.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/agents/{created.Agent.Id}/samples")).StatusCode);
    }

    [Fact]
    public async Task Un_observateur_ne_cree_pas_d_agent() =>
        Assert.Equal(HttpStatusCode.Forbidden,
            (await fixture.As(Guid.NewGuid(), OrganizationRoles.Viewer).PostAsJsonAsync("/api/agents", new { name = "x", environment = "Production" })).StatusCode);

    [Fact]
    public async Task Un_agent_silencieux_ouvre_une_panne_et_son_retour_la_termine()
    {
        var organization = Guid.NewGuid();
        var created = await RegisterAsync(organization);
        await AgentClient(created.Key).PostAsJsonAsync("/api/agents/ingest", Beat());

        async Task<List<string>> EventsAsync()
        {
            await using var db = fixture.NewDb();
            var all = await db.Set<OutboxMessage>().OrderBy(m => m.OccurredAt).ToListAsync();
            return all.Where(m => m.Payload.Contains(created.Agent.Id.ToString())).Select(m => m.RoutingKey).ToList();
        }

        // Cinq minutes plus tard, sans battement.
        await using (var db = fixture.NewDb())
        {
            var store = new AgentStore(db, new EfOutbox<MonitoringDb>(db, new OutboxSignal()), TimeProvider.System);
            // La détection porte sur tous les agents de la base ; les autres tests en ont créé aussi.
            Assert.True(await store.DetectSilentAsync(DateTimeOffset.UtcNow.AddMinutes(5), default) >= 1);
        }
        await using (var db = fixture.NewDb())
        {
            var store = new AgentStore(db, new EfOutbox<MonitoringDb>(db, new OutboxSignal()), TimeProvider.System);
            Assert.Equal(0, await store.DetectSilentAsync(DateTimeOffset.UtcNow.AddMinutes(6), default));
        }

        await AgentClient(created.Key).PostAsJsonAsync("/api/agents/ingest", Beat());

        Assert.Equal(
        [
            "monitoring.service.status-changed.v1",
            "monitoring.service.status-changed.v1", "monitoring.service.down-detected.v1",
            "monitoring.service.status-changed.v1", "monitoring.service.recovered.v1",
        ], await EventsAsync());

        await using var check = fixture.NewDb();
        var down = (await check.Set<OutboxMessage>().ToListAsync()).Single(m => m.EventType == "monitoring.service.down-detected" && m.Payload.Contains(created.Agent.Id.ToString()));
        Assert.Contains("\"source\":\"Agent\"", down.Payload.Replace(" ", ""));
        Assert.Contains("\"failure\":\"AgentSilent\"", down.Payload.Replace(" ", ""));
    }
}
