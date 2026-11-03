using System.Net;
using System.Net.Sockets;

namespace DevPulse.Security.Ssrf;

/// <summary>Dit si une adresse IP est joignable publiquement. Tout ce qui n'est pas public est refusé aux sondes.</summary>
public static class IpAddressPolicy
{
    private static readonly (IPAddress Network, int Prefix)[] BlockedIpv4 =
    [
        Range("0.0.0.0", 8),         // « ce réseau »
        Range("10.0.0.0", 8),        // privé
        Range("100.64.0.0", 10),     // NAT opérateur
        Range("127.0.0.0", 8),       // boucle locale
        Range("169.254.0.0", 16),    // lien local, dont les métadonnées cloud
        Range("172.16.0.0", 12),     // privé
        Range("192.0.0.0", 24),      // réservé IETF
        Range("192.0.2.0", 24),      // documentation
        Range("192.168.0.0", 16),    // privé
        Range("198.18.0.0", 15),     // bancs de test
        Range("198.51.100.0", 24),   // documentation
        Range("203.0.113.0", 24),    // documentation
        Range("224.0.0.0", 4),       // multidiffusion
        Range("240.0.0.0", 4),       // réservé, dont la diffusion générale
    ];

    // IPv6 : seul 2000::/3 (unicast global) est admis, moins les plages qui embarquent une adresse IPv4
    // arbitraire et permettraient de contourner la liste ci-dessus.
    private static readonly (IPAddress Network, int Prefix) GlobalUnicast = Range("2000::", 3);
    private static readonly (IPAddress Network, int Prefix)[] BlockedIpv6 =
    [
        Range("2001::", 32),         // Teredo
        Range("2001:db8::", 32),     // documentation
        Range("2002::", 16),         // 6to4
    ];

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !BlockedIpv4.Any(r => Contains(r, address)),
            AddressFamily.InterNetworkV6 => Contains(GlobalUnicast, address) && !BlockedIpv6.Any(r => Contains(r, address)),
            _ => false,
        };
    }

    private static (IPAddress, int) Range(string network, int prefix) => (IPAddress.Parse(network), prefix);

    private static bool Contains((IPAddress Network, int Prefix) range, IPAddress address)
    {
        var network = range.Network.GetAddressBytes();
        var candidate = address.GetAddressBytes();
        if (network.Length != candidate.Length) return false;

        var fullBytes = range.Prefix / 8;
        for (var i = 0; i < fullBytes; i++)
            if (network[i] != candidate[i]) return false;

        var remainingBits = range.Prefix % 8;
        if (remainingBits == 0) return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (network[fullBytes] & mask) == (candidate[fullBytes] & mask);
    }
}
