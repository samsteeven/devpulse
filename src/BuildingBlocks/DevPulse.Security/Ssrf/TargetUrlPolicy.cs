using System.Net;

namespace DevPulse.Security.Ssrf;

public sealed class TargetPolicyOptions
{
    public const string SectionName = "TargetPolicy";

    /// <summary>Autorise localhost et les réseaux privés. Réservé au poste de développement ; faux par défaut.</summary>
    public bool AllowPrivateTargets { get; set; }
}

/// <summary>
/// Contrôle statique d'une URL à surveiller. Il écarte les cas évidents dès l'enregistrement,
/// mais ne suffit pas : un nom de domaine peut résoudre vers une adresse privée. Le contrôle
/// qui fait foi a lieu à la connexion, sur l'adresse réellement résolue (<see cref="IpAddressPolicy"/>).
/// </summary>
public static class TargetUrlPolicy
{
    private static readonly int[] AllowedPorts = [80, 443, 8080, 8443];
    private static readonly string[] BlockedSuffixes = [".localhost", ".local", ".internal", ".intranet", ".lan", ".home", ".corp"];

    /// <summary>Renvoie null si l'URL est acceptable, sinon la raison du refus.</summary>
    public static string? Validate(Uri uri, bool allowPrivateTargets)
    {
        if (!uri.IsAbsoluteUri) return "L'URL doit être absolue.";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "Seuls http et https sont acceptés.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "L'URL ne doit pas contenir d'identifiants.";
        if (allowPrivateTargets) return null;

        if (!AllowedPorts.Contains(uri.Port)) return $"Port non autorisé (acceptés : {string.Join(", ", AllowedPorts)}).";

        // Uri normalise les écritures détournées d'une adresse (2130706433, 0x7f.1) : elles arrivent ici en IPv4.
        var host = uri.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(host, out var ip))
            return IpAddressPolicy.IsPublic(ip) ? null : "Cette adresse IP n'est pas publique.";

        if (host == "localhost" || BlockedSuffixes.Any(host.EndsWith)) return "Ce nom d'hôte désigne une ressource interne.";

        // Un nom sans point (postgres, rabbitmq) désigne un service du réseau interne, pas un site public.
        if (!host.Contains('.')) return "Le nom d'hôte doit être un nom de domaine complet.";

        return null;
    }
}
