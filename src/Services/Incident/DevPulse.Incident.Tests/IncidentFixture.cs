using DevPulse.Incident.Infrastructure;
using DevPulse.Messaging;
using DevPulse.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace DevPulse.Incident.Tests;

public sealed class IncidentFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = TestContainers.Postgres();
    private WebApplicationFactory<Program>? _factory;

    public TestJwt Jwt { get; } = new();

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.UseSetting("ConnectionStrings:Database", _db.GetConnectionString());
            host.UseSetting("Jwt:PublicKey", Jwt.PublicKey);
            host.UseSetting("Database:MigrateOnStartup", "true");
            // Les tests d'API ne parlent pas à RabbitMQ : l'outbox se remplit et on l'inspecte.
            host.UseSetting("RabbitMq:Enabled", "false");
        });
        _factory.CreateClient().Dispose();
    }

    public IncidentDb NewDb() => new(new DbContextOptionsBuilder<IncidentDb>()
        .UseNpgsql(_db.GetConnectionString()).UseSnakeCaseNamingConvention().Options);

    public EfOutbox<IncidentDb> Outbox(IncidentDb db) => new(db, new OutboxSignal());

    public HttpClient As(Guid organizationId, string role, Guid? userId = null)
    {
        var client = _factory!.CreateClient();
        Jwt.Authorize(client, organizationId, role, userId, "astreinte@example.test");
        return client;
    }

    public HttpClient Anonymous() => _factory!.CreateClient();

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _db.DisposeAsync();
        Jwt.Dispose();
    }
}

[CollectionDefinition(Name)]
public class IncidentCollection : ICollectionFixture<IncidentFixture>
{
    public const string Name = "incident";
}
