using DevPulse.Monitoring.Domain;
using DevPulse.Monitoring.Infrastructure;
using DevPulse.Security.Ssrf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DevPulse.Monitoring.Tests;

/// <summary>Vraies requêtes HTTP vers un vrai serveur Kestrel local, à travers le client des sondes.</summary>
public sealed class ProbeServer : IAsyncLifetime
{
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = "";
    public int Calls;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();

        _app.Use((_, next) => { Interlocked.Increment(ref Calls); return next(); });
        _app.MapGet("/ok", () => Results.Ok());
        _app.MapGet("/slow", async () => { await Task.Delay(1100); return Results.Ok(); });
        _app.MapGet("/hang", async (CancellationToken ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Results.Ok(); });
        _app.MapGet("/down", () => Results.StatusCode(503));
        _app.MapGet("/redirect", () => Results.Redirect("/ok"));

        await _app.StartAsync();
        BaseUrl = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}

public class HttpTargetProberTests(ProbeServer server) : IClassFixture<ProbeServer>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class Factory(SocketsHttpHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    private static HttpTargetProber Prober(bool allowPrivate)
    {
        var policy = new TargetPolicyOptions { AllowPrivateTargets = allowPrivate };
        return new HttpTargetProber(new Factory(HttpTargetProber.CreateHandler(policy)), Options.Create(policy), TimeProvider.System);
    }

    [Fact]
    public async Task Une_reponse_rapide_est_operationnelle()
    {
        var result = await Prober(true).ProbeAsync($"{server.BaseUrl}/ok", Timeout, default);

        Assert.Equal(HealthStatus.Operational, result.Status);
        Assert.Equal(200, result.HttpStatusCode);
    }

    [Fact]
    public async Task Une_reponse_lente_mais_correcte_est_degradee()
    {
        var result = await Prober(true).ProbeAsync($"{server.BaseUrl}/slow", Timeout, default);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.True(result.ResponseTimeMs >= 1000);
    }

    [Fact]
    public async Task Une_erreur_503_est_une_panne()
    {
        var result = await Prober(true).ProbeAsync($"{server.BaseUrl}/down", Timeout, default);

        Assert.Equal(HealthStatus.Down, result.Status);
        Assert.Equal(503, result.HttpStatusCode);
        Assert.Equal(ProbeFailure.UnexpectedStatus, result.Failure);
    }

    [Fact]
    public async Task Le_delai_depasse_est_une_panne_et_respecte_le_delai_demande()
    {
        var result = await Prober(true).ProbeAsync($"{server.BaseUrl}/hang", TimeSpan.FromMilliseconds(300), default);

        Assert.Equal(ProbeFailure.Timeout, result.Failure);
        Assert.InRange(result.ResponseTimeMs, 250, 3000);
    }

    [Fact]
    public async Task Une_redirection_n_est_jamais_suivie()
    {
        var result = await Prober(true).ProbeAsync($"{server.BaseUrl}/redirect", Timeout, default);

        Assert.Equal(HealthStatus.Down, result.Status);
        Assert.Equal(ProbeFailure.Redirect, result.Failure);
        Assert.InRange(result.HttpStatusCode!.Value, 300, 399);
    }

    [Fact]
    public async Task Un_port_ferme_est_une_erreur_de_connexion()
    {
        var result = await Prober(true).ProbeAsync("http://127.0.0.1:9/health", Timeout, default);

        Assert.Equal(ProbeFailure.ConnectionError, result.Failure);
    }

    [Fact]
    public async Task En_mode_normal_une_adresse_de_boucle_locale_n_est_jamais_contactee()
    {
        var before = Volatile.Read(ref server.Calls);
        var result = await Prober(false).ProbeAsync($"{server.BaseUrl}/ok", Timeout, default);

        Assert.Equal(ProbeFailure.BlockedTarget, result.Failure);
        Assert.Equal(before, Volatile.Read(ref server.Calls));
    }

    [Fact]
    public async Task Le_garde_fou_de_connexion_bloque_un_nom_public_qui_resout_vers_une_adresse_interne()
    {
        // Ce client n'a pas de contrôle statique : seul ConnectCallback peut arrêter la requête. « localhost » passe par une
        // vraie résolution DNS et donne une adresse interne, exactement la situation d'un DNS rebinding.
        var handler = HttpTargetProber.CreateHandler(new TargetPolicyOptions { AllowPrivateTargets = false });
        using var client = new HttpClient(handler);

        var port = new Uri(server.BaseUrl).Port;
        var before = Volatile.Read(ref server.Calls);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://localhost:{port}/ok"));

        Assert.IsType<BlockedTargetException>(exception.InnerException);
        Assert.Equal(before, Volatile.Read(ref server.Calls));
    }

    [Fact]
    public async Task L_arret_du_worker_ne_passe_pas_pour_une_panne_de_la_cible()
    {
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Prober(true).ProbeAsync($"{server.BaseUrl}/hang", Timeout, stopping.Token));
    }

    [Theory]
    [InlineData(200, 100, HealthStatus.Operational)]
    [InlineData(204, 999, HealthStatus.Operational)]
    [InlineData(200, 1000, HealthStatus.Degraded)]
    [InlineData(301, 10, HealthStatus.Down)]
    [InlineData(404, 10, HealthStatus.Down)]
    [InlineData(500, 10, HealthStatus.Down)]
    public void La_classification_suit_le_code_et_la_duree(int code, int ms, HealthStatus expected) =>
        Assert.Equal(expected, HealthClassifier.FromResponse(code, TimeSpan.FromMilliseconds(ms), DateTimeOffset.UtcNow).Status);
}
