using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using DevPulse.Monitoring.Infrastructure;
using DevPulse.Security.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace DevPulse.Monitoring.Tests;

public sealed class MonitoringFixture : IAsyncLifetime
{
    // Données en mémoire : plus rapide, et rien n'est écrit dans le disque virtuel de Docker, qui ne rétrécit jamais seul.
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17.11-alpine")
        .WithTmpfsMount("/var/lib/postgresql/data")
        .Build();
    private readonly RSA _rsa = RSA.Create(2048);
    private WebApplicationFactory<Program>? _factory;

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.UseSetting("ConnectionStrings:Database", _db.GetConnectionString());
            host.UseSetting("Jwt:PublicKey", Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo()));
            host.UseSetting("Database:MigrateOnStartup", "true");
            host.UseSetting("RabbitMq:Enabled", "false");
        });
        _factory.CreateClient().Dispose();
    }

    /// <summary>Un contexte neuf : ce qu'un autre processus (le worker) verrait.</summary>
    public MonitoringDb NewDb() => new(new DbContextOptionsBuilder<MonitoringDb>()
        .UseNpgsql(_db.GetConnectionString())
        .UseSnakeCaseNamingConvention()
        .Options);

    public HttpClient As(Guid organizationId, string role = OrganizationRoles.Viewer)
    {
        var client = _factory!.CreateClient();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "devpulse-identity",
            Audience = "devpulse-api",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa), SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(DevPulseClaims.Subject, Guid.NewGuid().ToString()),
                new Claim(DevPulseClaims.OrganizationId, organizationId.ToString()),
                new Claim(DevPulseClaims.OrganizationRole, role),
            ]),
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient Anonymous() => _factory!.CreateClient();

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _db.DisposeAsync();
        _rsa.Dispose();
    }
}

[CollectionDefinition(Name)]
public class MonitoringCollection : ICollectionFixture<MonitoringFixture>
{
    public const string Name = "monitoring";
}
