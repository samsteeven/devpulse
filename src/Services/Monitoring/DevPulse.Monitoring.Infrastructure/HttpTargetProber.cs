using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using DevPulse.Monitoring.Application;
using DevPulse.Monitoring.Domain;
using DevPulse.Security.Ssrf;
using Microsoft.Extensions.Options;

namespace DevPulse.Monitoring.Infrastructure;

public sealed class BlockedTargetException(string host) : Exception($"Cible refusée : {host}");

public sealed class HttpTargetProber(IHttpClientFactory clients, IOptions<TargetPolicyOptions> policy, TimeProvider clock) : ITargetProber
{
    public const string ClientName = "probe";

    public async Task<ProbeResult> ProbeAsync(string targetUrl, TimeSpan timeout, CancellationToken ct)
    {
        var checkedAt = clock.GetUtcNow();

        // Registry a déjà contrôlé cette URL ; le worker ne lui fait pas confiance pour autant.
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) || TargetUrlPolicy.Validate(uri, policy.Value.AllowPrivateTargets) is not null)
            return HealthClassifier.FromFailure(ProbeFailure.BlockedTarget, TimeSpan.Zero, checkedAt);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("DevPulse-Probe/1.0");

            // Seuls les en-têtes sont lus : un corps énorme ne peut ni saturer le worker ni fausser la mesure.
            using var response = await clients.CreateClient(ClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            return HealthClassifier.FromResponse((int)response.StatusCode, stopwatch.Elapsed, checkedAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // arrêt du worker : ce n'est pas une panne de la cible
        }
        catch (OperationCanceledException)
        {
            return HealthClassifier.FromFailure(ProbeFailure.Timeout, stopwatch.Elapsed, checkedAt);
        }
        catch (HttpRequestException e)
        {
            return HealthClassifier.FromFailure(Classify(e), stopwatch.Elapsed, checkedAt);
        }
    }

    private static ProbeFailure Classify(HttpRequestException exception)
    {
        for (Exception? inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is BlockedTargetException) return ProbeFailure.BlockedTarget;
            if (inner is AuthenticationException) return ProbeFailure.TlsError;
        }
        return ProbeFailure.ConnectionError;
    }

    /// <summary>
    /// Client des sondes. Le contrôle SSRF qui fait foi est ici : il porte sur l'adresse à laquelle la connexion
    /// va réellement se faire, ce qui protège aussi contre le « DNS rebinding » (un nom public au moment de
    /// l'enregistrement, qui résout ensuite vers une adresse interne).
    /// </summary>
    public static SocketsHttpHandler CreateHandler(TargetPolicyOptions policy) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        MaxResponseHeadersLength = 64, // Ko
        // Pas de connexion réutilisée : chaque sonde refait la résolution DNS, le contrôle et la mesure complète.
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = (context, ct) => ConnectToAllowedAddressAsync(context.DnsEndPoint, policy, ct),
    };

    private static async ValueTask<Stream> ConnectToAllowedAddressAsync(DnsEndPoint endpoint, TargetPolicyOptions policy, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, ct);

        // Une seule adresse interne suffit à tout refuser : on ne choisit pas parmi les réponses d'un DNS suspect.
        if (!policy.AllowPrivateTargets && addresses.Any(a => !IpAddressPolicy.IsPublic(a)))
            throw new BlockedTargetException(endpoint.Host);

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, endpoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                last = e;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
}
