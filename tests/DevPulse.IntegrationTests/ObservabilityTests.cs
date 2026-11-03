using System.Diagnostics.Metrics;
using DevPulse.Observability;

namespace DevPulse.IntegrationTests;

public class ObservabilityTests
{
    [Theory]
    [InlineData("DevPulse.Messaging", "devpulse.messaging.consumed", true)]
    [InlineData("DevPulse.Alerting", "devpulse.alerts.notification.latency", true)]
    [InlineData("Microsoft.AspNetCore.Hosting", "http.server.request.duration", true)]
    [InlineData("System.Runtime", "dotnet.gc.collections", false)]
    [InlineData("System.Net.Http", "http.client.request.duration", false)]
    [InlineData("Microsoft.AspNetCore.Server.Kestrel", "kestrel.active_connections", false)]
    public void Vers_Azure_seules_les_mesures_utiles_aux_alertes_partent(string meter, string instrument, bool kept)
    {
        using var source = new Meter(meter);
        Assert.Equal(kept, ObservabilityExtensions.KeptForAzure(source.CreateCounter<long>(instrument)));
    }
}
