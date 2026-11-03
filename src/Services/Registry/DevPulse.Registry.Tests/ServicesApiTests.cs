using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevPulse.Contracts.Internal;
using DevPulse.Registry.Application;
using DevPulse.Security.Authentication;

namespace DevPulse.Registry.Tests;

[Collection(RegistryCollection.Name)]
public class ServicesApiTests(RegistryFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static object Payload(string name = "Payment API", string baseUrl = "https://api.example.com", string path = "/health") => new
    {
        name,
        environment = "Production",
        baseUrl,
        healthCheckPath = path,
        checkIntervalSeconds = 60,
        timeoutSeconds = 10,
    };

    private static async Task<ServiceResponse> CreateAsync(HttpClient client, string name = "Payment API")
    {
        var response = await client.PostAsJsonAsync("/api/services", Payload(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ServiceResponse>(Json))!;
    }

    [Fact]
    public async Task Sans_jeton_toutes_les_routes_repondent_401()
    {
        var client = fixture.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/services")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/services", Payload())).StatusCode);
    }

    [Fact]
    public async Task Un_jeton_signe_par_une_autre_cle_est_refuse()
    {
        using var other = RSA.Create(2048);
        var client = fixture.Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.CreateToken(Guid.NewGuid(), OrganizationRoles.Owner, other));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/services")).StatusCode);
    }

    [Fact]
    public async Task Le_parcours_complet_cree_lit_modifie_et_supprime()
    {
        var client = fixture.As(Guid.NewGuid());
        var created = await CreateAsync(client);
        Assert.Equal("https://api.example.com", created.BaseUrl);
        Assert.True(created.Enabled);

        var read = await client.GetFromJsonAsync<ServiceResponse>($"/api/services/{created.Id}", Json);
        Assert.Equal("Payment API", read!.Name);

        var updated = await client.PutAsJsonAsync($"/api/services/{created.Id}", Payload("Payment API v2"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Payment API v2", (await updated.Content.ReadFromJsonAsync<ServiceResponse>(Json))!.Name);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/services/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/services/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Une_organisation_ne_voit_ni_ne_modifie_les_services_d_une_autre()
    {
        var alice = fixture.As(Guid.NewGuid());
        var mallory = fixture.As(Guid.NewGuid());
        var service = await CreateAsync(alice);

        Assert.Empty((await mallory.GetFromJsonAsync<List<ServiceResponse>>("/api/services", Json))!);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.GetAsync($"/api/services/{service.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.PutAsJsonAsync($"/api/services/{service.Id}", Payload("Volé"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.DeleteAsync($"/api/services/{service.Id}")).StatusCode);

        var intact = await alice.GetFromJsonAsync<ServiceResponse>($"/api/services/{service.Id}", Json);
        Assert.Equal("Payment API", intact!.Name);
    }

    [Theory]
    [InlineData(OrganizationRoles.Viewer)]
    [InlineData(OrganizationRoles.Developer)]
    public async Task Un_role_en_lecture_lit_mais_ne_modifie_pas(string role)
    {
        var organization = Guid.NewGuid();
        var service = await CreateAsync(fixture.As(organization));
        var reader = fixture.As(organization, role);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/services/{service.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/services", Payload("Autre"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/services/{service.Id}")).StatusCode);
    }

    [Fact]
    public async Task Le_meme_nom_dans_le_meme_environnement_repond_409_mais_reste_libre_ailleurs()
    {
        var client = fixture.As(Guid.NewGuid());
        await CreateAsync(client);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/services", Payload())).StatusCode);
        await CreateAsync(fixture.As(Guid.NewGuid())); // une autre organisation peut reprendre ce nom
    }

    [Theory]
    [InlineData("http://localhost:5432", "/health")]
    [InlineData("http://169.254.169.254", "/latest/meta-data")]
    [InlineData("http://192.168.1.1", "/health")]
    [InlineData("http://postgres", "/health")]
    [InlineData("ftp://api.example.com", "/health")]
    [InlineData("https://user:secret@api.example.com", "/health")]
    [InlineData("https://api.example.com:6379", "/health")]
    [InlineData("https://api.example.com", "//169.254.169.254/latest")]
    [InlineData("https://api.example.com", "sans-barre")]
    public async Task Une_cible_interne_ou_malformee_est_refusee_a_l_enregistrement(string baseUrl, string path)
    {
        var client = fixture.As(Guid.NewGuid());
        var response = await client.PostAsJsonAsync("/api/services", Payload(baseUrl: baseUrl, path: path));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Un_environnement_inconnu_est_refuse_meme_sous_forme_de_nombre()
    {
        var client = fixture.As(Guid.NewGuid());
        foreach (object environment in new object[] { "Moon", 99 })
        {
            var response = await client.PostAsJsonAsync("/api/services", new { name = "Payment API", environment, baseUrl = "https://api.example.com" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task La_route_interne_exige_la_cle_et_ne_renvoie_que_les_services_actifs()
    {
        var organization = Guid.NewGuid();
        var client = fixture.As(organization);
        var active = await CreateAsync(client, "Actif");
        var disabled = await CreateAsync(client, "Désactivé");
        var body = new { name = "Désactivé", environment = "Production", baseUrl = "https://api.example.com", enabled = false };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/services/{disabled.Id}", body)).StatusCode);

        var anonymous = fixture.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/internal/services/active")).StatusCode);

        // Un jeton utilisateur valide n'ouvre pas la route interne.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/internal/services/active")).StatusCode);

        var worker = fixture.Anonymous();
        worker.DefaultRequestHeaders.Add(InternalApiOptions.HeaderName, RegistryFixture.InternalKey);
        var list = await worker.GetFromJsonAsync<List<ActiveServiceDto>>("/internal/services/active");

        var mine = list!.Where(s => s.OrganizationId == organization).ToList();
        var only = Assert.Single(mine);
        Assert.Equal(active.Id, only.ServiceId);
        Assert.Equal("https://api.example.com/health", only.TargetUrl);
    }
}
