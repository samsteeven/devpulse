using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevPulse.Alerting.Application;
using DevPulse.Alerting.Domain;
using DevPulse.Alerting.Infrastructure;
using DevPulse.Contracts.Events.Incident;
using DevPulse.Security.Authentication;
using DevPulse.Security.Ssrf;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevPulse.Alerting.Tests;

public class AlertingDomainTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Un_canal_ne_retient_que_ses_evenements_et_ses_environnements()
    {
        var channel = AlertChannel.Create(Guid.NewGuid(), "Astreinte", ChannelKind.Webhook, "https://hooks.example.com/x",
            [AlertEvent.IncidentCreated], ["Production"], T0);

        Assert.True(channel.Accepts(AlertEvent.IncidentCreated, "Production"));
        Assert.False(channel.Accepts(AlertEvent.IncidentCreated, "Staging"));
        Assert.False(channel.Accepts(AlertEvent.IncidentResolved, "Production"));
        Assert.Equal(64, channel.SigningSecret.Length);
    }

    [Fact]
    public void Un_envoi_en_echec_est_repris_avec_un_delai_croissant_puis_abandonne()
    {
        var channel = AlertChannel.Create(Guid.NewGuid(), "Astreinte", ChannelKind.Slack, "https://hooks.example.com/x", [AlertEvent.IncidentCreated], [], T0);
        var delivery = AlertDelivery.Create(channel, Guid.NewGuid(), AlertEvent.IncidentCreated, "API", "{}", T0, T0);

        var delays = new List<TimeSpan>();
        for (var i = 0; i < AlertDelivery.RetryDelays.Length; i++)
        {
            delivery.MarkFailed(500, "HTTP 500", T0);
            delays.Add(delivery.NextAttemptAt - T0);
            Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        }

        Assert.Equal(AlertDelivery.RetryDelays, delays);
        delivery.MarkFailed(500, "HTTP 500", T0);
        Assert.Equal(DeliveryStatus.Failed, delivery.Status);
    }

    [Theory]
    [InlineData("", "Le nom")]
    [InlineData("ok", "Environnements")]
    public void Une_configuration_invalide_est_refusee(string name, string expected)
    {
        var error = Assert.Throws<DomainException>(() => AlertChannel.Create(Guid.NewGuid(), name, ChannelKind.Webhook, "https://x.example.com",
            [AlertEvent.IncidentCreated], ["Prod"], T0));
        Assert.StartsWith(expected, error.Message);
    }
}

[Collection(AlertingCollection.Name)]
public class AlertingTests(AlertingFixture fixture)
{
    private sealed class Factory(TargetPolicyOptions policy) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(SafeHttpHandler.Create(policy)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static HttpAlertSender Sender(bool allowPrivate) => new(new Factory(new TargetPolicyOptions { AllowPrivateTargets = allowPrivate }), TimeProvider.System);

    private static IncidentCreatedV1 Created(Guid organizationId, string environment = "Production") => new()
    {
        Producer = "incident-service", OrganizationId = organizationId, CorrelationId = "corr", IncidentId = Guid.NewGuid(),
        ServiceId = Guid.NewGuid(), ServiceName = "Payment API", Environment = environment, Severity = "Critical",
        Title = "Payment API ne répond plus", Source = "Probe", Failure = "UnexpectedStatus", FailureCount = 2, HttpStatusCode = 503,
        DetectedAt = DateTimeOffset.UtcNow.AddSeconds(-2), OpenedAt = DateTimeOffset.UtcNow,
    };

    private sealed class NoRealtime : IRealtimeNotifier
    {
        public Task ServiceStatusChangedAsync(Guid organizationId, ServiceStatusNotification notification, CancellationToken ct) => Task.CompletedTask;
        public Task IncidentChangedAsync(Guid organizationId, IncidentNotification notification, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Le_webhook_est_signe_et_la_signature_se_verifie()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        var result = await Sender(allowPrivate: true).SendAsync(
            new OutgoingAlert(Guid.NewGuid(), ChannelKind.Webhook, receiver.Url, "secret-de-test", AlertEvent.IncidentCreated, """{"a":1}"""), default);

        Assert.True(result.Success);
        var (headers, body) = Assert.Single(receiver.Received);
        var timestamp = headers["X-DevPulse-Timestamp"].ToString();
        Assert.Equal("sha256=" + HttpAlertSender.Sign("secret-de-test", timestamp, body), headers["X-DevPulse-Signature"].ToString());
        Assert.NotEqual("sha256=" + HttpAlertSender.Sign("autre-secret", timestamp, body), headers["X-DevPulse-Signature"].ToString());
    }

    [Fact]
    public async Task Une_adresse_interne_n_est_jamais_contactee_meme_si_elle_est_en_base()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        var result = await Sender(allowPrivate: false).SendAsync(
            new OutgoingAlert(Guid.NewGuid(), ChannelKind.Webhook, receiver.Url, "s", AlertEvent.IncidentCreated, "{}"), default);

        Assert.False(result.Success);
        Assert.Contains("politique de sécurité", result.Error);
        Assert.Empty(receiver.Received);
    }

    [Fact]
    public async Task Seuls_les_canaux_concernes_recoivent_un_envoi_puis_l_expediteur_les_livre()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        var organization = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var db = fixture.NewDb())
        {
            db.Channels.Add(AlertChannel.Create(organization, "Prod", ChannelKind.Webhook, receiver.Url, [AlertEvent.IncidentCreated], ["Production"], now));
            db.Channels.Add(AlertChannel.Create(organization, "Staging", ChannelKind.Webhook, receiver.Url, [AlertEvent.IncidentCreated], ["Staging"], now));
            db.Channels.Add(AlertChannel.Create(organization, "Résolutions", ChannelKind.Slack, receiver.Url, [AlertEvent.IncidentResolved], [], now));
            db.Channels.Add(AlertChannel.Create(Guid.NewGuid(), "Autre organisation", ChannelKind.Webhook, receiver.Url, [AlertEvent.IncidentCreated], [], now));
            await db.SaveChangesAsync();
        }

        var created = Created(organization);
        await using (var db = fixture.NewDb())
        {
            await new IncidentEventsHandler(new AlertChannelRepository(db), new DeliveryRepository(db), new NoRealtime(), new DeliverySignal(), TimeProvider.System)
                .HandleAsync(created, default);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.NewDb())
            Assert.Equal(1, await db.Deliveries.CountAsync(d => d.IncidentId == created.IncidentId));

        // L'expéditeur, avec un vrai client HTTP, contre le destinataire local.
        var services = new ServiceCollection()
            .AddScoped(_ => fixture.NewDb())
            .BuildServiceProvider();
        var dispatcher = new DeliveryDispatcher(services.GetRequiredService<IServiceScopeFactory>(), Sender(allowPrivate: true),
            new DeliverySignal(), TimeProvider.System, NullLogger<DeliveryDispatcher>.Instance);
        await dispatcher.StartAsync(default);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (receiver.Received.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(100);
        await dispatcher.StopAsync(default);

        var (_, body) = Assert.Single(receiver.Received);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("incident.created", json.GetProperty("event").GetString());
        Assert.Equal("UnexpectedStatus, HTTP 503", json.GetProperty("detail").GetString());

        await using var check = fixture.NewDb();
        var delivery = await check.Deliveries.SingleAsync(d => d.IncidentId == created.IncidentId);
        Assert.Equal(DeliveryStatus.Sent, delivery.Status);
        Assert.InRange(delivery.LatencyMilliseconds!.Value, 0, 10_000);
    }

    [Fact]
    public async Task Un_destinataire_en_erreur_programme_une_reprise()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        receiver.StatusCode = 503;
        var organization = Guid.NewGuid();
        Guid deliveryId;
        await using (var db = fixture.NewDb())
        {
            var channel = AlertChannel.Create(organization, "Prod", ChannelKind.Webhook, receiver.Url, [AlertEvent.IncidentCreated], [], DateTimeOffset.UtcNow);
            var delivery = AlertDelivery.Create(channel, Guid.NewGuid(), AlertEvent.IncidentCreated, "API", "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            deliveryId = delivery.Id;
            db.Channels.Add(channel);
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection().AddScoped(_ => fixture.NewDb()).BuildServiceProvider();
        var dispatcher = new DeliveryDispatcher(services.GetRequiredService<IServiceScopeFactory>(), Sender(allowPrivate: true),
            new DeliverySignal(), TimeProvider.System, NullLogger<DeliveryDispatcher>.Instance);
        await dispatcher.StartAsync(default);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (receiver.Received.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(100);
        await Task.Delay(300);
        await dispatcher.StopAsync(default);

        await using var check = fixture.NewDb();
        var stored = await check.Deliveries.SingleAsync(d => d.Id == deliveryId);
        Assert.Equal(DeliveryStatus.Pending, stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal(503, stored.LastStatusCode);
        Assert.True(stored.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task La_cle_de_signature_n_est_montree_qu_a_la_creation()
    {
        var owner = fixture.As(Guid.NewGuid(), OrganizationRoles.Owner);
        var created = await owner.PostAsJsonAsync("/api/alert-channels", new
        {
            name = "Astreinte", kind = "Webhook", url = "https://hooks.example.com/devpulse", events = new[] { "IncidentCreated" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(64, (await created.Content.ReadFromJsonAsync<ChannelResponse>())!.SigningSecret!.Length);

        var listed = Assert.Single((await owner.GetFromJsonAsync<List<ChannelResponse>>("/api/alert-channels"))!);
        Assert.Null(listed.SigningSecret);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9000/hook")]
    [InlineData("https://169.254.169.254/latest")]
    [InlineData("http://hooks.example.com/devpulse")]
    public async Task Une_url_de_webhook_dangereuse_ou_non_chiffree_est_refusee(string url)
    {
        var response = await fixture.As(Guid.NewGuid(), OrganizationRoles.Owner).PostAsJsonAsync("/api/alert-channels", new
        {
            name = "Piège", kind = "Webhook", url, events = new[] { "IncidentCreated" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Un_canal_d_une_autre_organisation_est_introuvable_et_un_observateur_ne_cree_rien()
    {
        var alice = fixture.As(Guid.NewGuid(), OrganizationRoles.Admin);
        var created = await (await alice.PostAsJsonAsync("/api/alert-channels", new
        {
            name = "Alice", kind = "Slack", url = "https://hooks.slack.example.com/T0", events = new[] { "IncidentResolved" },
        })).Content.ReadFromJsonAsync<ChannelResponse>();

        var bob = fixture.As(Guid.NewGuid(), OrganizationRoles.Owner);
        Assert.Empty((await bob.GetFromJsonAsync<List<ChannelResponse>>("/api/alert-channels"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/alert-channels/{created!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/api/alert-channels/{created.Id}/test", null)).StatusCode);

        var viewer = fixture.As(Guid.NewGuid(), OrganizationRoles.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/alert-channels", new
        {
            name = "x", kind = "Slack", url = "https://hooks.slack.example.com/T0", events = new[] { "IncidentResolved" },
        })).StatusCode);
    }

    private HubConnection Hub(Guid organizationId) => new HubConnectionBuilder()
        .WithUrl(new Uri(fixture.Factory.Server.BaseAddress, "/hubs/notifications"), options =>
        {
            options.HttpMessageHandlerFactory = _ => fixture.Factory.Server.CreateHandler();
            options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            options.AccessTokenProvider = () => Task.FromResult<string?>(fixture.Jwt.Create(organizationId, OrganizationRoles.Viewer));
        })
        .Build();

    [Fact]
    public async Task Le_temps_reel_ne_parvient_qu_a_l_organisation_concernee()
    {
        var alice = Guid.NewGuid();
        await using var aliceHub = Hub(alice);
        await using var bobHub = Hub(Guid.NewGuid());
        var aliceReceived = new TaskCompletionSource<IncidentNotification>();
        var bobReceived = false;
        aliceHub.On<IncidentNotification>("incidentChanged", n => aliceReceived.TrySetResult(n));
        bobHub.On<IncidentNotification>("incidentChanged", _ => bobReceived = true);
        await aliceHub.StartAsync();
        await bobHub.StartAsync();

        var notifier = fixture.Factory.Services.GetRequiredService<IRealtimeNotifier>();
        await notifier.IncidentChangedAsync(alice, new IncidentNotification(Guid.NewGuid(), Guid.NewGuid(), "Payment API", "created", "Critical", "t", DateTimeOffset.UtcNow), default);

        var received = await aliceReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("created", received.Change);
        await Task.Delay(500);
        Assert.False(bobReceived);
    }

    [Fact]
    public async Task Le_hub_refuse_une_connexion_sans_jeton()
    {
        await using var anonymous = new HubConnectionBuilder()
            .WithUrl(new Uri(fixture.Factory.Server.BaseAddress, "/hubs/notifications"), options =>
            {
                options.HttpMessageHandlerFactory = _ => fixture.Factory.Server.CreateHandler();
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => anonymous.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }
}
