using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using DevPulse.Security.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DevPulse.IntegrationTests;

/// <summary>La vraie passerelle devant de faux services qui disent simplement qui a reçu la requête.</summary>
public sealed class GatewayFixture : IAsyncLifetime
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly List<WebApplication> _backends = [];
    private WebApplicationFactory<Program>? _factory;

    public async Task InitializeAsync()
    {
        var identity = await StartBackendAsync("identity");
        var registry = await StartBackendAsync("registry");
        var monitoring = await StartBackendAsync("monitoring");
        var incident = await StartBackendAsync("incident");
        var alerting = await StartBackendAsync("alerting");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.UseSetting("Jwt:PublicKey", Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo()));
            host.UseSetting("RateLimiting:AuthPermitPerMinute", "1000");
            host.UseSetting("ReverseProxy:Clusters:identity:Destinations:main:Address", identity);
            host.UseSetting("ReverseProxy:Clusters:registry:Destinations:main:Address", registry);
            host.UseSetting("ReverseProxy:Clusters:monitoring:Destinations:main:Address", monitoring);
            host.UseSetting("ReverseProxy:Clusters:incident:Destinations:main:Address", incident);
            host.UseSetting("ReverseProxy:Clusters:alerting:Destinations:main:Address", alerting);
        });
    }

    /// <summary>Une passerelle à part, avec une limite basse, pour vérifier le dépassement.</summary>
    public WebApplicationFactory<Program> WithAuthLimit(int permits) => _factory!.WithWebHostBuilder(host =>
        host.UseSetting("RateLimiting:AuthPermitPerMinute", permits.ToString()));

    private async Task<string> StartBackendAsync(string name)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(context => context.Response.WriteAsJsonAsync(new Echo(name, context.Request.Path, context.Request.Headers["X-Correlation-Id"].ToString())));
        await app.StartAsync();
        _backends.Add(app);
        return app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/";
    }

    public HttpClient Anonymous() => _factory!.CreateClient();

    public HttpClient Authenticated()
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
                new Claim(DevPulseClaims.OrganizationId, Guid.NewGuid().ToString()),
                new Claim(DevPulseClaims.OrganizationRole, OrganizationRoles.Owner),
            ]),
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        foreach (var backend in _backends) await backend.DisposeAsync();
        _rsa.Dispose();
    }
}

public sealed record Echo(string Service, string Path, string CorrelationId);

public class GatewayTests(GatewayFixture fixture) : IClassFixture<GatewayFixture>
{
    [Theory]
    [InlineData("/api/services", "registry")]
    [InlineData("/api/services/7f3d1c9e-0000-4000-8000-000000000001", "registry")]
    [InlineData("/api/services/7f3d1c9e-0000-4000-8000-000000000001/health", "monitoring")]
    [InlineData("/api/services/7f3d1c9e-0000-4000-8000-000000000001/metrics", "monitoring")]
    [InlineData("/api/dashboard/overview", "monitoring")]
    [InlineData("/api/auth/me", "identity")]
    [InlineData("/api/auth/organizations", "identity")]
    [InlineData("/api/organization/members", "identity")]
    [InlineData("/api/incidents", "incident")]
    [InlineData("/api/incidents/7f3d1c9e-0000-4000-8000-000000000001", "incident")]
    [InlineData("/api/alert-channels", "alerting")]
    [InlineData("/api/alert-deliveries", "alerting")]
    [InlineData("/api/agents", "monitoring")]
    [InlineData("/hubs/notifications/negotiate", "alerting")]
    public async Task Chaque_route_arrive_au_bon_service(string path, string expected)
    {
        var echo = await fixture.Authenticated().GetFromJsonAsync<Echo>(path);

        Assert.Equal(expected, echo!.Service);
        Assert.Equal(path, echo.Path);
    }

    [Theory]
    [InlineData("/api/services")]
    [InlineData("/api/dashboard/overview")]
    [InlineData("/api/auth/me")]
    [InlineData("/api/incidents")]
    [InlineData("/api/alert-channels")]
    [InlineData("/api/organization/members")]
    [InlineData("/api/agents")]
    [InlineData("/hubs/notifications/negotiate")]
    public async Task Sans_jeton_la_passerelle_arrete_la_requete(string path) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Anonymous().GetAsync(path)).StatusCode);

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/register")]
    [InlineData("/api/auth/refresh")]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/invitations/preview")]
    [InlineData("/api/auth/invitations/accept")]
    public async Task La_connexion_et_l_inscription_passent_sans_jeton(string path)
    {
        var response = await fixture.Anonymous().PostAsJsonAsync(path, new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("identity", (await response.Content.ReadFromJsonAsync<Echo>())!.Service);
    }

    /// <summary>Un agent n'a pas de jeton utilisateur : c'est Monitoring qui vérifie sa clé.</summary>
    [Fact]
    public async Task Le_battement_d_un_agent_passe_sans_jeton_mais_pas_le_reste_des_agents()
    {
        var ingest = await fixture.Anonymous().PostAsJsonAsync("/api/agents/ingest", new { });
        Assert.Equal("monitoring", (await ingest.Content.ReadFromJsonAsync<Echo>())!.Service);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Anonymous().GetAsync("/api/agents/ingest")).StatusCode);
    }

    [Fact]
    public async Task Au_dela_de_la_limite_la_connexion_repond_429()
    {
        using var gateway = fixture.WithAuthLimit(2);
        var client = gateway.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/auth/login", new { })).StatusCode);
        // Une route sans limite propre n'est pas touchée.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/services")).StatusCode);
    }

    [Fact]
    public async Task Seul_le_post_de_connexion_est_public() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Anonymous().GetAsync("/api/auth/login")).StatusCode);

    [Theory]
    [InlineData("/internal/services/active")]
    [InlineData("/api/internal/services/active")]
    public async Task La_route_interne_de_Registry_n_est_pas_exposee(string path)
    {
        var response = await fixture.Authenticated().GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Un_identifiant_de_correlation_valide_est_transmis_et_renvoye()
    {
        var client = fixture.Authenticated();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "abc12345-test");
        var response = await client.GetAsync("/api/services");

        Assert.Equal("abc12345-test", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("abc12345-test", (await response.Content.ReadFromJsonAsync<Echo>())!.CorrelationId);
    }

    [Fact]
    public async Task Un_identifiant_de_correlation_douteux_est_remplace()
    {
        var client = fixture.Authenticated();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Correlation-Id", "x<script>alert(1)</script>");
        var echo = await client.GetFromJsonAsync<Echo>("/api/services");

        Assert.Matches("^[0-9a-f]{32}$", echo!.CorrelationId);
    }

    [Fact]
    public async Task Les_en_tetes_de_securite_sont_presents()
    {
        var response = await fixture.Anonymous().GetAsync("/api/services");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }
}
