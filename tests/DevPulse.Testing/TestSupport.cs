using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using DevPulse.Security.Authentication;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace DevPulse.Testing;

public static class TestContainers
{
    /// <summary>Données en mémoire : plus rapide, et rien n'est écrit dans le disque virtuel de Docker, qui ne rétrécit jamais seul.</summary>
    public static PostgreSqlContainer Postgres() => new PostgreSqlBuilder("postgres:17.11-alpine")
        .WithTmpfsMount("/var/lib/postgresql/data")
        .Build();
}

/// <summary>Jetons signés par une clé RSA jetable, au format qu'émet Identity.</summary>
public sealed class TestJwt : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);

    public string PublicKey => Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());

    public string Create(Guid organizationId, string role, Guid? userId = null, string? email = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "devpulse-identity",
            Audience = "devpulse-api",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa), SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(DevPulseClaims.Subject, (userId ?? Guid.NewGuid()).ToString()),
                new Claim("email", email ?? "test@example.test"),
                new Claim(DevPulseClaims.OrganizationId, organizationId.ToString()),
                new Claim(DevPulseClaims.OrganizationRole, role),
            ]),
        });

    public void Authorize(HttpClient client, Guid organizationId, string role, Guid? userId = null, string? email = null) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Create(organizationId, role, userId, email));

    public void Dispose() => _rsa.Dispose();
}
