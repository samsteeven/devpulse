using System.Net.Http.Headers;
using System.Security.Cryptography;
using DevPulse.Registry.Infrastructure;
using DevPulse.Security.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using Testcontainers.PostgreSql;

namespace DevPulse.Registry.Tests;

/// <summary>PostgreSQL réel (Testcontainers) et l'API complète, avec une clé RSA jetable.</summary>
public sealed class RegistryFixture : IAsyncLifetime
{
    public const string InternalKey = "cle-interne-de-test";

    // Données en mémoire : plus rapide, et rien n'est écrit dans le disque virtuel de Docker, qui ne rétrécit jamais seul.
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17.11-alpine")
        .WithTmpfsMount("/var/lib/postgresql/data")
        .Build();
    private readonly RSA _rsa = RSA.Create(2048);
    private WebApplicationFactory<Program>? _factory;

    public async Task InitializeAsync()
    {
        await _db.StartAsync();

        var publicKey = Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.UseSetting("ConnectionStrings:Database", _db.GetConnectionString());
            host.UseSetting("Jwt:PublicKey", publicKey);
            host.UseSetting("InternalApi:Key", InternalKey);
            host.UseSetting("Database:MigrateOnStartup", "true");
        });

        // Démarre l'hôte, ce qui applique les migrations.
        _factory.CreateClient().Dispose();
    }

    public HttpClient Anonymous() => _factory!.CreateClient();

    public HttpClient As(Guid organizationId, string role = OrganizationRoles.Owner)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(organizationId, role));
        return client;
    }

    public string CreateToken(Guid organizationId, string role, RSA? signingKey = null, string issuer = "devpulse-identity")
    {
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = "devpulse-api",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(signingKey ?? _rsa), SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(DevPulseClaims.Subject, Guid.NewGuid().ToString()),
                new Claim(DevPulseClaims.OrganizationId, organizationId.ToString()),
                new Claim(DevPulseClaims.OrganizationRole, role),
            ]),
        });
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _db.DisposeAsync();
        _rsa.Dispose();
    }
}

[CollectionDefinition(Name)]
public class RegistryCollection : ICollectionFixture<RegistryFixture>
{
    public const string Name = "registry";
}
