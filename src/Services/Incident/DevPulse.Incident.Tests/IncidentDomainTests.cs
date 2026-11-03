using DevPulse.Incident.Domain;
using IncidentEntity = DevPulse.Incident.Domain.Incident;

namespace DevPulse.Incident.Tests;

public class IncidentDomainTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly Actor Alice = new(Guid.NewGuid(), "alice@example.test");

    private static IncidentEntity Open(string environment = "Production") => IncidentEntity.Open(
        new Detection(Guid.NewGuid(), Guid.NewGuid(), "Payment API", environment, "Probe", "Timeout", 2, null, 10000, T0), T0.AddSeconds(1));

    [Theory]
    [InlineData("Production", IncidentSeverity.Critical)]
    [InlineData("Staging", IncidentSeverity.Major)]
    [InlineData("Development", IncidentSeverity.Minor)]
    public void La_gravite_depend_de_l_environnement(string environment, IncidentSeverity expected) =>
        Assert.Equal(expected, Open(environment).Severity);

    [Fact]
    public void Prendre_en_charge_deux_fois_ne_change_rien()
    {
        var incident = Open();
        Assert.True(incident.Acknowledge(Alice, T0.AddMinutes(1)));
        Assert.False(incident.Acknowledge(Alice, T0.AddMinutes(2)));
        Assert.Equal(T0.AddMinutes(1), incident.AcknowledgedAt);
        Assert.Equal(2, incident.Timeline.Count);
    }

    [Fact]
    public void Un_incident_resolu_ne_peut_plus_etre_pris_en_charge()
    {
        var incident = Open();
        incident.Resolve(Alice, "redémarré", T0.AddMinutes(5));
        Assert.Throws<InvalidTransitionException>(() => incident.Acknowledge(Alice, T0.AddMinutes(6)));
        Assert.False(incident.Resolve(Alice, null, T0.AddMinutes(7)));
    }

    [Fact]
    public void Une_reprise_anterieure_a_la_detection_ne_ferme_pas_l_incident()
    {
        var incident = Open();
        Assert.False(incident.ResolveAutomatically(T0.AddMinutes(-1)));
        Assert.Equal(IncidentStatus.Open, incident.Status);

        Assert.True(incident.ResolveAutomatically(T0.AddMinutes(3)));
        Assert.True(incident.ResolvedAutomatically);
        Assert.Equal(180, incident.DurationSeconds(T0.AddHours(1)));
    }

    [Fact]
    public void Seule_la_premiere_analyse_est_conservee()
    {
        var incident = Open();
        Assert.True(incident.AttachAnalysis(new AnalysisProposal("Rules", null, "Délai dépassé", ["Saturation"], ["Vérifier la charge"], T0.AddMinutes(1))));
        Assert.False(incident.AttachAnalysis(new AnalysisProposal("Llm", "m", "Autre", [], [], T0.AddMinutes(2))));
        Assert.Equal("Délai dépassé", incident.AnalysisSummary);
    }

    [Fact]
    public void Une_note_vide_ou_trop_longue_est_refusee()
    {
        var incident = Open();
        Assert.Throws<DomainException>(() => incident.AddNote(Alice, "  ", T0));
        Assert.Throws<DomainException>(() => incident.AddNote(Alice, new string('x', 2001), T0));
    }

    [Fact]
    public void Une_panne_plus_ancienne_que_la_derniere_reprise_est_ignoree()
    {
        var state = ServiceOutageState.For(Guid.NewGuid(), Guid.NewGuid());
        state.RecordRecovery(T0.AddMinutes(5));
        Assert.False(state.RecordDetection(T0));
        Assert.True(state.RecordDetection(T0.AddMinutes(6)));
    }
}
