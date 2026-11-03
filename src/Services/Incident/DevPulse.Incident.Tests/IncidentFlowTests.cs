using System.Net;
using System.Net.Http.Json;
using DevPulse.Contracts.Events.AI;
using DevPulse.Contracts.Events.Monitoring;
using DevPulse.Incident.Application;
using DevPulse.Incident.Infrastructure;
using DevPulse.Messaging;
using DevPulse.Security.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevPulse.Incident.Tests;

/// <summary>Les traitements d'événements sur une vraie base, puis l'API qui en expose le résultat.</summary>
[Collection(IncidentCollection.Name)]
public class IncidentFlowTests(IncidentFixture fixture)
{
    private static ServiceDownDetectedV1 Down(Guid organizationId, Guid serviceId, DateTimeOffset at, string environment = "Production") => new()
    {
        Producer = "monitoring-service", OrganizationId = organizationId, CorrelationId = "corr-" + Guid.NewGuid().ToString("N")[..8],
        ServiceId = serviceId, ServiceName = "Payment API", Environment = environment, Source = MonitoringSources.Probe,
        FailureCount = 2, Failure = "UnexpectedStatus", LastHttpStatusCode = 503, LastResponseTimeMs = 40, DetectedAt = at,
    };

    private static ServiceRecoveredV1 Recovered(Guid organizationId, Guid serviceId, DateTimeOffset since, DateTimeOffset at) => new()
    {
        Producer = "monitoring-service", OrganizationId = organizationId, CorrelationId = "corr-r",
        ServiceId = serviceId, ServiceName = "Payment API", Environment = "Production", DownSince = since, RecoveredAt = at,
    };

    /// <summary>Ce que fait le consommateur : traitement puis SaveChanges, dans un contexte neuf.</summary>
    private async Task HandleAsync(ServiceDownDetectedV1 e)
    {
        await using var db = fixture.NewDb();
        await new ServiceDownDetectedHandler(new IncidentRepository(db), fixture.Outbox(db), TimeProvider.System, NullLogger<ServiceDownDetectedHandler>.Instance)
            .HandleAsync(e, default);
        await db.SaveChangesAsync();
    }

    private async Task HandleAsync(ServiceRecoveredV1 e)
    {
        await using var db = fixture.NewDb();
        await new ServiceRecoveredHandler(new IncidentRepository(db), fixture.Outbox(db), NullLogger<ServiceRecoveredHandler>.Instance).HandleAsync(e, default);
        await db.SaveChangesAsync();
    }

    private async Task HandleAsync(AnalysisCompletedV1 e)
    {
        await using var db = fixture.NewDb();
        await new AnalysisCompletedHandler(new IncidentRepository(db), fixture.Outbox(db)).HandleAsync(e, default);
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> EventsForAsync(Guid organizationId)
    {
        await using var db = fixture.NewDb();
        var all = await db.Set<OutboxMessage>().OrderBy(m => m.OccurredAt).ToListAsync();
        return all.Where(m => m.Payload.Contains(organizationId.ToString())).Select(m => m.RoutingKey).ToList();
    }

    [Fact]
    public async Task Une_panne_ouvre_un_seul_incident_et_sa_reprise_le_ferme()
    {
        var organization = Guid.NewGuid();
        var service = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);

        await HandleAsync(Down(organization, service, t0));
        await HandleAsync(Down(organization, service, t0.AddMinutes(1))); // nouvelle annonce pendant la même panne
        await HandleAsync(Recovered(organization, service, t0, t0.AddMinutes(4)));

        var list = await fixture.As(organization, OrganizationRoles.Viewer).GetFromJsonAsync<List<IncidentSummary>>("/api/incidents");
        var incident = Assert.Single(list!);
        Assert.Equal("Resolved", incident.Status);
        Assert.True(incident.ResolvedAutomatically);
        Assert.Equal("Critical", incident.Severity);
        Assert.Equal(240, incident.DurationSeconds);

        Assert.Equal(["incident.incident.created.v1", "incident.incident.resolved.v1"], await EventsForAsync(organization));
    }

    [Fact]
    public async Task Une_reprise_recue_avant_la_panne_n_ouvre_pas_d_incident()
    {
        var organization = Guid.NewGuid();
        var service = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);

        await HandleAsync(Recovered(organization, service, t0, t0.AddMinutes(2)));
        await HandleAsync(Down(organization, service, t0));

        var list = await fixture.As(organization, OrganizationRoles.Viewer).GetFromJsonAsync<List<IncidentSummary>>("/api/incidents");
        Assert.Empty(list!);
    }

    [Fact]
    public async Task L_analyse_est_rattachee_une_fois_et_visible_dans_le_detail()
    {
        var organization = Guid.NewGuid();
        await HandleAsync(Down(organization, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1), "Staging"));
        var client = fixture.As(organization, OrganizationRoles.Viewer);
        var id = (await client.GetFromJsonAsync<List<IncidentSummary>>("/api/incidents"))!.Single().Id;

        var analysis = new AnalysisCompletedV1
        {
            Producer = "ai-worker", OrganizationId = organization, CorrelationId = "corr-ai", IncidentId = id,
            Provider = "Rules", Summary = "Le service renvoie 503.", ProbableCauses = ["Surcharge"], SuggestedActions = ["Vérifier les journaux"],
            GeneratedAt = DateTimeOffset.UtcNow,
        };
        await HandleAsync(analysis);
        await HandleAsync(analysis with { EventId = Guid.NewGuid(), Summary = "Doublon" });

        var details = await client.GetFromJsonAsync<IncidentDetails>($"/api/incidents/{id}");
        Assert.Equal("Le service renvoie 503.", details!.Analysis!.Summary);
        Assert.Equal(["Surcharge"], details.Analysis.ProbableCauses);
        Assert.Contains(details.Timeline, t => t.Kind == "Analysis");
        Assert.Equal(1, (await EventsForAsync(organization)).Count(k => k == "incident.incident.analyzed.v1"));
    }

    [Fact]
    public async Task Un_observateur_lit_mais_ne_prend_pas_en_charge()
    {
        var organization = Guid.NewGuid();
        await HandleAsync(Down(organization, Guid.NewGuid(), DateTimeOffset.UtcNow));
        var viewer = fixture.As(organization, OrganizationRoles.Viewer);
        var id = (await viewer.GetFromJsonAsync<List<IncidentSummary>>("/api/incidents"))!.Single().Id;

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/incidents/{id}/acknowledge", null)).StatusCode);

        var developer = fixture.As(organization, OrganizationRoles.Developer);
        var acknowledged = await developer.PostAsync($"/api/incidents/{id}/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
        var details = await acknowledged.Content.ReadFromJsonAsync<IncidentDetails>();
        Assert.Equal("Acknowledged", details!.Incident.Status);
        Assert.Equal("astreinte@example.test", details.Timeline.Last().Author);

        var resolved = await developer.PostAsJsonAsync($"/api/incidents/{id}/resolve", new ResolveRequest("Pool de connexions agrandi"));
        Assert.Equal("Resolved", (await resolved.Content.ReadFromJsonAsync<IncidentDetails>())!.Incident.Status);

        // Prendre en charge un incident résolu : conflit, pas erreur serveur.
        Assert.Equal(HttpStatusCode.Conflict, (await developer.PostAsync($"/api/incidents/{id}/acknowledge", null)).StatusCode);
        Assert.Equal(
            ["incident.incident.created.v1", "incident.incident.acknowledged.v1", "incident.incident.resolved.v1"],
            await EventsForAsync(organization));
    }

    [Fact]
    public async Task Un_incident_d_une_autre_organisation_n_existe_pas()
    {
        var alice = Guid.NewGuid();
        await HandleAsync(Down(alice, Guid.NewGuid(), DateTimeOffset.UtcNow));
        var id = (await fixture.As(alice, OrganizationRoles.Viewer).GetFromJsonAsync<List<IncidentSummary>>("/api/incidents"))!.Single().Id;

        var bob = fixture.As(Guid.NewGuid(), OrganizationRoles.Owner);
        Assert.Empty((await bob.GetFromJsonAsync<List<IncidentSummary>>("/api/incidents"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/incidents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/api/incidents/{id}/acknowledge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/api/incidents/{id}/resolve", new ResolveRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/api/incidents/{id}/notes", new NoteRequest("x"))).StatusCode);
    }

    [Fact]
    public async Task Les_statistiques_comptent_les_incidents_et_la_duree_moyenne()
    {
        var organization = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        var first = Guid.NewGuid();
        await HandleAsync(Down(organization, first, t0));
        await HandleAsync(Recovered(organization, first, t0, t0.AddMinutes(10)));
        await HandleAsync(Down(organization, Guid.NewGuid(), t0));

        var stats = await fixture.As(organization, OrganizationRoles.Viewer).GetFromJsonAsync<IncidentStatsResponse>("/api/incidents/stats");
        Assert.Equal(1, stats!.Open);
        Assert.Equal(1, stats.ResolvedInPeriod);
        Assert.Equal(600, stats.MeanTimeToResolveSeconds);
    }

    [Fact]
    public async Task Sans_jeton_tout_est_refuse() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Anonymous().GetAsync("/api/incidents")).StatusCode);

    [Fact]
    public async Task Un_filtre_inconnu_est_refuse() =>
        Assert.Equal(HttpStatusCode.BadRequest,
            (await fixture.As(Guid.NewGuid(), OrganizationRoles.Viewer).GetAsync("/api/incidents?status=nimportequoi")).StatusCode);
}
