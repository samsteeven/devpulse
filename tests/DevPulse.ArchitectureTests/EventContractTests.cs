using System.Text.Json;
using System.Text.RegularExpressions;
using DevPulse.Contracts.Events.Monitoring;

namespace DevPulse.ArchitectureTests;

public partial class EventContractTests
{
    [GeneratedRegex(@"^[a-z]+\.[a-z]+\.[a-z-]+\.v\d+$")]
    private static partial Regex RoutingKeyFormat();

    private static readonly ServiceDownDetectedV1 Sample = new()
    {
        Producer = "monitoring-service",
        OrganizationId = Guid.NewGuid(),
        CorrelationId = "c3d4e5f6",
        ServiceId = Guid.NewGuid(),
        FailureCount = 3,
        LastHttpStatusCode = 503,
        DetectedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void La_cle_de_routage_suit_la_convention_en_quatre_segments()
    {
        Assert.Equal("monitoring.service.down-detected.v1", Sample.RoutingKey);
        Assert.Matches(RoutingKeyFormat(), Sample.RoutingKey);
    }

    /// <summary>Le blueprint liait « monitoring.service.* » : en AMQP, « * » vaut un seul mot, et ce motif n'aurait jamais rien reçu.</summary>
    [Theory]
    [InlineData("monitoring.service.*", false)]
    [InlineData("monitoring.service.*.v1", true)]
    [InlineData("monitoring.service.down-detected.v1", true)]
    [InlineData("monitoring.service.#", true)]
    public void Le_motif_de_liaison_doit_couvrir_les_quatre_segments(string binding, bool expected) =>
        Assert.Equal(expected, TopicMatches(binding, Sample.RoutingKey));

    [Fact]
    public void L_enveloppe_contient_les_champs_du_contrat()
    {
        var json = JsonDocument.Parse(JsonSerializer.Serialize(Sample, JsonSerializerOptions.Web)).RootElement;

        foreach (var field in new[] { "eventId", "eventType", "version", "occurredAt", "producer", "correlationId", "organizationId", "serviceId" })
            Assert.True(json.TryGetProperty(field, out _), $"Champ manquant : {field}");

        Assert.Equal("monitoring.service.down-detected", json.GetProperty("eventType").GetString());
        Assert.Equal(1, json.GetProperty("version").GetInt32());
    }

    // Sémantique des échanges « topic » : « * » remplace exactement un mot, « # » zéro ou plusieurs.
    private static bool TopicMatches(string binding, string routingKey)
    {
        var pattern = "^" + string.Join(@"\.", binding.Split('.').Select(word => word switch
        {
            "*" => @"[^.]+",
            "#" => @".*",
            _ => Regex.Escape(word),
        })) + "$";
        return Regex.IsMatch(routingKey, pattern);
    }
}
