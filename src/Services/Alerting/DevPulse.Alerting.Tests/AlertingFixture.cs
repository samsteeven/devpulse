using System.Collections.Concurrent;
using DevPulse.Alerting.Infrastructure;
using DevPulse.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace DevPulse.Alerting.Tests;

public sealed class AlertingFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = TestContainers.Postgres();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public TestJwt Jwt { get; } = new();

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.UseSetting("ConnectionStrings:Database", _db.GetConnectionString());
            host.UseSetting("Jwt:PublicKey", Jwt.PublicKey);
            host.UseSetting("Database:MigrateOnStartup", "true");
            host.UseSetting("RabbitMq:Enabled", "false");
            // Comme en production : les webhooks vers localhost ou un réseau privé sont refusés.
            host.UseSetting("TargetPolicy:AllowPrivateTargets", "false");
        });
        Factory.CreateClient().Dispose();
    }

    public AlertingDb NewDb() => new(new DbContextOptionsBuilder<AlertingDb>()
        .UseNpgsql(_db.GetConnectionString()).UseSnakeCaseNamingConvention().Options);

    public HttpClient As(Guid organizationId, string role)
    {
        var client = Factory.CreateClient();
        Jwt.Authorize(client, organizationId, role);
        return client;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _db.DisposeAsync();
        Jwt.Dispose();
    }
}

[CollectionDefinition(Name)]
public class AlertingCollection : ICollectionFixture<AlertingFixture>
{
    public const string Name = "alerting";
}

/// <summary>Un vrai serveur HTTP local qui joue le destinataire d'un webhook.</summary>
public sealed class WebhookReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;

    public ConcurrentQueue<(IHeaderDictionary Headers, string Body)> Received { get; } = new();
    public int StatusCode { get; set; } = 200;
    public string Url { get; }

    private WebhookReceiver(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public static async Task<WebhookReceiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        WebhookReceiver? receiver = null;
        app.MapPost("/hook", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            receiver!.Received.Enqueue((new HeaderDictionary(context.Request.Headers.ToDictionary(h => h.Key, h => h.Value)), await reader.ReadToEndAsync()));
            return Results.StatusCode(receiver.StatusCode);
        });
        await app.StartAsync();
        receiver = new WebhookReceiver(app, app.Urls.Single() + "/hook");
        return receiver;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
