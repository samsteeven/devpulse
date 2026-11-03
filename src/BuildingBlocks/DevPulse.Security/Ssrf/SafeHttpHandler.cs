using System.Net;
using System.Net.Sockets;

namespace DevPulse.Security.Ssrf;

public sealed class BlockedTargetException(string host) : Exception($"Cible refusée : {host}");

/// <summary>
/// Client HTTP pour toute URL fournie par un utilisateur (sondes, webhooks). Le contrôle SSRF qui fait foi est ici :
/// il porte sur l'adresse à laquelle la connexion va réellement se faire, ce qui protège aussi contre le
/// « DNS rebinding » (un nom public au moment de l'enregistrement, qui résout ensuite vers une adresse interne).
/// </summary>
public static class SafeHttpHandler
{
    public static SocketsHttpHandler Create(TargetPolicyOptions policy) => new()
    {
        // Une redirection pourrait viser une ressource interne : elle n'est jamais suivie.
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        MaxResponseHeadersLength = 64, // Ko
        // Pas de connexion réutilisée : chaque appel refait la résolution DNS et le contrôle.
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
