using System.Diagnostics;
using System.Security.Authentication;
using DevPulse.Monitoring.Application;
using DevPulse.Monitoring.Domain;
using DevPulse.Security.Ssrf;
using Microsoft.Extensions.Options;

namespace DevPulse.Monitoring.Infrastructure;

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

    /// <summary>Client des sondes : le contrôle SSRF à la connexion est dans <see cref="SafeHttpHandler"/>.</summary>
    public static SocketsHttpHandler CreateHandler(TargetPolicyOptions policy) => SafeHttpHandler.Create(policy);
}
