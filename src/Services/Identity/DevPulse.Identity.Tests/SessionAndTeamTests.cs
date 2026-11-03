using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using DevPulse.Identity.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Testcontainers.PostgreSql;

namespace DevPulse.Identity.Tests;

public sealed class SessionFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17.11-alpine").WithTmpfsMount("/var/lib/postgresql/data").Build();
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
            // Le serveur de test parle en http : un cookie Secure n'y serait jamais renvoyé.
            host.UseSetting("Auth:SecureCookie", "false");
        });
        _factory.CreateClient().Dispose();
    }

    /// <summary>Un navigateur : il garde ses cookies d'une requête à l'autre.</summary>
    public HttpClient Browser()
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Requested-With", "DevPulse");
        return client;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _db.DisposeAsync();
        _rsa.Dispose();
    }
}

public class SessionAndTeamTests(SessionFixture fixture) : IClassFixture<SessionFixture>
{
    private const string Password = "un-mot-de-passe-solide";

    private static string NewEmail() => $"{Guid.NewGuid():N}@example.test";

    private static void UseToken(HttpClient client, AuthResponse session) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

    private async Task<(HttpClient Browser, AuthResponse Session)> RegisterAsync(string? email = null, string organization = "Atelier Nova")
    {
        var browser = fixture.Browser();
        var response = await browser.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email ?? NewEmail(), Password, organization));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(browser, session);
        return (browser, session);
    }

    private static string? RefreshCookieOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(v => v.StartsWith("devpulse_refresh=")) : null;

    [Fact]
    public async Task Le_cookie_de_renouvellement_est_httponly_strict_et_limite_a_l_authentification()
    {
        var response = await fixture.Browser().PostAsJsonAsync("/api/auth/register", new RegisterRequest(NewEmail(), Password, "Atelier"));
        var cookie = RefreshCookieOf(response)!.ToLowerInvariant();

        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/api/auth", cookie);
    }

    [Fact]
    public async Task Le_renouvellement_donne_un_nouveau_jeton_et_remplace_le_cookie()
    {
        var (browser, first) = await RegisterAsync();
        var refreshed = await browser.PostAsync("/api/auth/refresh", null);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var session = (await refreshed.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.NotEqual(first.AccessToken, session.AccessToken);
        Assert.Equal(first.Organization.Id, session.Organization.Id);
        Assert.NotNull(RefreshCookieOf(refreshed));
    }

    [Fact]
    public async Task Rejouer_un_jeton_deja_utilise_revoque_toute_la_famille()
    {
        // Le voleur garde une copie du cookie actuel.
        var register = await fixture.Browser().PostAsJsonAsync("/api/auth/register", new RegisterRequest(NewEmail(), Password, "Copie"));
        var stolenCookie = RefreshCookieOf(register)!.Split(';')[0];

        var thief = fixture.Browser();
        thief.DefaultRequestHeaders.Add("Cookie", stolenCookie);
        var victim = fixture.Browser();
        victim.DefaultRequestHeaders.Add("Cookie", stolenCookie);

        // La victime renouvelle : le jeton copié est désormais « déjà échangé ».
        var victimRefresh = await victim.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, victimRefresh.StatusCode);
        var victimNewCookie = RefreshCookieOf(victimRefresh)!.Split(';')[0];

        // Le voleur présente l'ancien : refusé, et toute la famille tombe.
        Assert.Equal(HttpStatusCode.Unauthorized, (await thief.PostAsync("/api/auth/refresh", null)).StatusCode);

        var victimAgain = fixture.Browser();
        victimAgain.DefaultRequestHeaders.Add("Cookie", victimNewCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await victimAgain.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Apres_deconnexion_le_cookie_ne_vaut_plus_rien()
    {
        var register = await fixture.Browser().PostAsJsonAsync("/api/auth/register", new RegisterRequest(NewEmail(), Password, "Atelier"));
        var cookie = RefreshCookieOf(register)!.Split(';')[0];
        var browser = fixture.Browser();
        browser.DefaultRequestHeaders.Add("Cookie", cookie);

        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Le_renouvellement_exige_l_en_tete_de_l_application()
    {
        var (browser, _) = await RegisterAsync();
        browser.DefaultRequestHeaders.Remove("X-Requested-With");
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Une_invitation_cree_le_compte_et_donne_le_role_prevu_une_seule_fois()
    {
        var (owner, ownerSession) = await RegisterAsync(organization: "Équipe Orion");
        var email = NewEmail();

        var invitation = await (await owner.PostAsJsonAsync("/api/organization/invitations", new { email, role = "Developer" }))
            .Content.ReadFromJsonAsync<InvitationCreated>();

        var guest = fixture.Browser();
        var preview = await (await guest.PostAsJsonAsync("/api/auth/invitations/preview", new InvitationTokenRequest(invitation!.Token)))
            .Content.ReadFromJsonAsync<InvitationPreview>();
        Assert.Equal("Équipe Orion", preview!.OrganizationName);
        Assert.False(preview.AccountExists);

        var accepted = await guest.PostAsJsonAsync("/api/auth/invitations/accept", new AcceptInvitationRequest(invitation.Token, Password));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var session = (await accepted.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.Equal(ownerSession.Organization.Id, session.Organization.Id);
        Assert.Equal("Developer", new JsonWebToken(session.AccessToken).GetClaim("org_role").Value);

        // Le même lien ne sert pas deux fois.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await fixture.Browser().PostAsJsonAsync("/api/auth/invitations/accept", new AcceptInvitationRequest(invitation.Token, Password))).StatusCode);

        var members = await owner.GetFromJsonAsync<List<MemberResponse>>("/api/organization/members");
        Assert.Contains(members!, m => m.Email == email && m.Role == "Developer");
    }

    [Fact]
    public async Task Un_compte_existant_doit_prouver_son_mot_de_passe_puis_change_d_organisation()
    {
        var (owner, ownerSession) = await RegisterAsync(organization: "Équipe Vega");
        var email = NewEmail();
        var (guest, guestSession) = await RegisterAsync(email, "Perso");

        var invitation = await (await owner.PostAsJsonAsync("/api/organization/invitations", new { email, role = "Viewer" }))
            .Content.ReadFromJsonAsync<InvitationCreated>();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.PostAsJsonAsync("/api/auth/invitations/accept", new AcceptInvitationRequest(invitation!.Token, "pas-le-bon-mot-de-passe"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await guest.PostAsJsonAsync("/api/auth/invitations/accept", new AcceptInvitationRequest(invitation.Token, Password))).StatusCode);

        var organizations = await guest.GetFromJsonAsync<List<OrganizationDto>>("/api/auth/organizations");
        Assert.Equal(2, organizations!.Count);

        var switched = await guest.PostAsJsonAsync("/api/auth/switch", new SwitchOrganizationRequest(guestSession.Organization.Id));
        Assert.Equal(guestSession.Organization.Id, (await switched.Content.ReadFromJsonAsync<AuthResponse>())!.Organization.Id);

        // Impossible de « basculer » vers une organisation dont on n'est pas membre.
        var (_, stranger) = await RegisterAsync(organization: "Ailleurs");
        Assert.Equal(HttpStatusCode.NotFound,
            (await guest.PostAsJsonAsync("/api/auth/switch", new SwitchOrganizationRequest(stranger.Organization.Id))).StatusCode);
        Assert.NotEqual(ownerSession.Organization.Id, stranger.Organization.Id);
    }

    [Fact]
    public async Task Un_administrateur_ne_cree_pas_de_proprietaire_et_le_dernier_proprietaire_reste()
    {
        var (owner, ownerSession) = await RegisterAsync();
        var adminEmail = NewEmail();
        var invitation = await (await owner.PostAsJsonAsync("/api/organization/invitations", new { email = adminEmail, role = "Admin" }))
            .Content.ReadFromJsonAsync<InvitationCreated>();
        var admin = fixture.Browser();
        var adminSession = (await (await admin.PostAsJsonAsync("/api/auth/invitations/accept", new AcceptInvitationRequest(invitation!.Token, Password)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(admin, adminSession);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/organization/invitations", new { email = NewEmail(), role = "Owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.DeleteAsync($"/api/organization/members/{ownerSession.UserId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await owner.PutAsJsonAsync($"/api/organization/members/{ownerSession.UserId}", new { role = "Viewer" })).StatusCode);
    }

    [Fact]
    public async Task Un_membre_retire_ne_peut_plus_renouveler_sa_session()
    {
        var (owner, _) = await RegisterAsync();
        var email = NewEmail();
        var invitation = await (await owner.PostAsJsonAsync("/api/organization/invitations", new { email, role = "Developer" }))
            .Content.ReadFromJsonAsync<InvitationCreated>();
        var member = fixture.Browser();
        var memberSession = (await (await member.PostAsJsonAsync("/api/auth/invitations/accept", new AcceptInvitationRequest(invitation!.Token, Password)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/organization/members/{memberSession.UserId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Les_membres_d_une_autre_organisation_restent_invisibles()
    {
        var (alice, _) = await RegisterAsync();
        var (bob, bobSession) = await RegisterAsync();

        var aliceMembers = await alice.GetFromJsonAsync<List<MemberResponse>>("/api/organization/members");
        Assert.DoesNotContain(aliceMembers!, m => m.UserId == bobSession.UserId);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync($"/api/organization/members/{bobSession.UserId}")).StatusCode);
        bob.Dispose();
    }
}
