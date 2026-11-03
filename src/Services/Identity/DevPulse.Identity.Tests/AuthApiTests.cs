using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using DevPulse.Identity.Application;
using DevPulse.Identity.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Testcontainers.PostgreSql;

namespace DevPulse.Identity.Tests;

public sealed class IdentityFixture : IAsyncLifetime
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
            host.UseSetting("Jwt:PrivateKey", Convert.ToBase64String(_rsa.ExportPkcs8PrivateKey()));
            host.UseSetting("Database:MigrateOnStartup", "true");
        });
        _factory.CreateClient().Dispose();
    }

    public HttpClient Client() => _factory!.CreateClient();

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _db.DisposeAsync();
        _rsa.Dispose();
    }
}

public class AuthApiTests(IdentityFixture fixture) : IClassFixture<IdentityFixture>
{
    private const string Password = "un-mot-de-passe-solide";

    private static string NewEmail() => $"{Guid.NewGuid():N}@example.test";

    private async Task<AuthResponse> RegisterAsync(string email)
    {
        var response = await fixture.Client().PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password, "Atelier Nova"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    [Fact]
    public async Task L_inscription_cree_une_organisation_dont_l_utilisateur_est_proprietaire()
    {
        var session = await RegisterAsync(NewEmail());

        Assert.Equal("Atelier Nova", session.Organization.Name);
        Assert.StartsWith("atelier-nova-", session.Organization.Slug);
        Assert.Equal("Owner", session.Organization.Role);

        var token = new JsonWebToken(session.AccessToken);
        Assert.Equal("RS256", token.Alg);
        Assert.Equal(session.UserId.ToString(), token.Subject);
        Assert.Equal(session.Organization.Id.ToString(), token.GetClaim("org_id").Value);
        Assert.Equal("Owner", token.GetClaim("org_role").Value);
        Assert.True(token.ValidTo <= DateTime.UtcNow.AddMinutes(16), "Le jeton d'accès doit être de courte durée.");
    }

    [Fact]
    public async Task La_connexion_permet_de_lire_son_profil()
    {
        var email = NewEmail();
        await RegisterAsync(email);

        var login = await fixture.Client().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = (await login.Content.ReadFromJsonAsync<AuthResponse>())!;

        var client = fixture.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var me = await client.GetFromJsonAsync<CurrentUserResponse>("/api/auth/me");
        Assert.Equal(email, me!.Email);
        Assert.Equal(session.Organization.Id, me.Organization.Id);
    }

    [Fact]
    public async Task Une_meme_adresse_ne_s_inscrit_pas_deux_fois()
    {
        var email = NewEmail();
        await RegisterAsync(email);

        var second = await fixture.Client().PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password, "Autre"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Un_mot_de_passe_trop_court_est_refuse()
    {
        var response = await fixture.Client().PostAsJsonAsync("/api/auth/register", new { email = NewEmail(), password = "court", organizationName = "Org" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Une_adresse_inconnue_et_un_mauvais_mot_de_passe_repondent_pareil()
    {
        var email = NewEmail();
        await RegisterAsync(email);

        var unknown = await fixture.Client().PostAsJsonAsync("/api/auth/login", new LoginRequest(NewEmail(), Password));
        var wrong = await fixture.Client().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "pas-le-bon-mot-de-passe"));

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync().ContinueWith(t => Normalize(t.Result)), await wrong.Content.ReadAsStringAsync().ContinueWith(t => Normalize(t.Result)));
    }

    [Fact]
    public async Task Le_compte_se_verrouille_apres_cinq_echecs_meme_avec_le_bon_mot_de_passe_ensuite()
    {
        var email = NewEmail();
        await RegisterAsync(email);

        for (var i = 0; i < 5; i++)
            await fixture.Client().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "pas-le-bon-mot-de-passe"));

        var login = await fixture.Client().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Le_profil_exige_un_jeton()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client().GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("Atelier Éphémère", "atelier-ephemere-")]
    [InlineData("  A & B  ", "a-b-")]
    public void Le_slug_est_lisible_et_unique(string name, string expectedPrefix)
    {
        var first = Organization.Create(name, DateTimeOffset.UtcNow);
        var second = Organization.Create(name, DateTimeOffset.UtcNow);

        Assert.StartsWith(expectedPrefix, first.Slug);
        Assert.NotEqual(first.Slug, second.Slug);
    }

    [Fact]
    public void Un_nom_sans_lettre_ni_chiffre_est_refuse() =>
        Assert.Throws<DomainException>(() => Organization.Create("&&&", DateTimeOffset.UtcNow));

    // traceId mis à part, le corps ne doit pas permettre de distinguer les deux cas.
    private static string Normalize(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", "");
}
