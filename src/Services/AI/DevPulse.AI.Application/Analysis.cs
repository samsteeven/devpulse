using System.Text;
using DevPulse.Contracts.Events.AI;
using DevPulse.Contracts.Events.Incident;
using DevPulse.Contracts.Messaging;
using Microsoft.Extensions.Logging;

namespace DevPulse.AI.Application;

/// <summary>
/// Ce qui est transmis à un fournisseur d'analyse, et rien d'autre : ni URL, ni nom d'hôte, ni identifiant
/// d'organisation. Le nom du service est saisi par l'utilisateur : il est nettoyé et tronqué avant l'envoi.
/// </summary>
public sealed record IncidentContext(
    string ServiceName, string Environment, string Source, string Failure, int FailureCount,
    int? HttpStatusCode, int? ResponseTimeMs, DateTimeOffset DetectedAt)
{
    public static IncidentContext From(IncidentCreatedV1 e) => new(
        Sanitize(e.ServiceName, 100), Sanitize(e.Environment, 20), Sanitize(e.Source, 20), Sanitize(e.Failure, 40),
        e.FailureCount, e.HttpStatusCode, e.ResponseTimeMs, e.DetectedAt);

    /// <summary>Retire les caractères de contrôle (retours à la ligne compris) : une saisie ne peut pas se faire passer pour une consigne.</summary>
    public static string Sanitize(string value, int max)
    {
        var clean = new StringBuilder(Math.Min(value.Length, max));
        foreach (var c in value)
        {
            if (clean.Length >= max) break;
            clean.Append(char.IsControl(c) ? ' ' : c);
        }
        return clean.ToString().Trim();
    }
}

public sealed record AnalysisResult(string Provider, string? Model, string Summary, IReadOnlyList<string> ProbableCauses, IReadOnlyList<string> SuggestedActions);

/// <summary>Fournisseur d'analyse (ADR-007). Ses propositions sont affichées, jamais exécutées.</summary>
public interface IAIAnalysisProvider
{
    string Name { get; }
    Task<AnalysisResult> AnalyzeAsync(IncidentContext context, CancellationToken ct);
}

/// <summary>Règles locales : sans clé, sans réseau, déterministes. Utilisées seules ou en repli d'un modèle de langage.</summary>
public sealed class RuleBasedProvider : IAIAnalysisProvider
{
    public const string ProviderName = "Rules";

    public string Name => ProviderName;

    public Task<AnalysisResult> AnalyzeAsync(IncidentContext c, CancellationToken ct)
    {
        var (summary, causes, actions) = c.Failure switch
        {
            "Timeout" => (
                $"{c.ServiceName} ne répond pas dans le délai imparti{Ms(c)}.",
                new[] { "Service saturé (processeur, threads, pool de connexions)", "Dépendance lente (base de données, API tierce)", "Réseau dégradé entre DevPulse et le service" },
                new[] { "Comparer la latence et la charge des dernières minutes", "Vérifier l'état des dépendances du service", "N'allonger le délai que si cette lenteur est normale" }),
            "ConnectionError" => (
                $"La connexion à {c.ServiceName} est refusée ou impossible.",
                new[] { "Processus arrêté ou en boucle de redémarrage", "Port fermé ou pare-feu modifié", "Nom de domaine qui ne résout plus" },
                new[] { "Vérifier que le service tourne et lire ses journaux de démarrage", "Contrôler les règles réseau et le répartiteur de charge", "Vérifier l'enregistrement DNS" }),
            "TlsError" => (
                $"La négociation TLS avec {c.ServiceName} échoue.",
                new[] { "Certificat expiré", "Nom du certificat différent du nom d'hôte", "Chaîne de certificats incomplète" },
                new[] { "Vérifier la date d'expiration du certificat", "Comparer le nom d'hôte et les noms du certificat", "Renouveler le certificat et redéployer la chaîne complète" }),
            "Redirect" => (
                $"Le point de contrôle de {c.ServiceName} répond par une redirection, que DevPulse ne suit pas.",
                new[] { "Passage de http à https", "Route de santé déplacée ou protégée par une page de connexion" },
                new[] { "Enregistrer l'URL finale (https, bon chemin)", "Exposer une route de santé publique qui répond 200" }),
            "BlockedTarget" => (
                $"L'adresse de {c.ServiceName} est refusée par la politique de sécurité de DevPulse.",
                new[] { "Le nom résout vers une adresse privée ou interne" },
                new[] { "Surveiller ce service avec un agent installé dans son réseau" }),
            "AgentSilent" => (
                $"L'agent de {c.ServiceName} n'envoie plus de données.",
                new[] { "Machine arrêtée ou injoignable", "Agent arrêté ou bloqué", "Clé de l'agent révoquée, ou sortie réseau bloquée" },
                new[] { "Vérifier que la machine répond", "Redémarrer l'agent et lire ses journaux", "Vérifier que la machine peut joindre DevPulse en HTTPS" }),
            _ => ForStatus(c),
        };

        return Task.FromResult(new AnalysisResult(ProviderName, null, summary, causes, actions));
    }

    private static (string, string[], string[]) ForStatus(IncidentContext c) => c.HttpStatusCode switch
    {
        500 => ($"{c.ServiceName} renvoie une erreur interne (HTTP 500).",
            ["Exception non gérée dans le code", "Dépendance en erreur (base de données, configuration)"],
            ["Lire les journaux d'erreur du service à l'heure de la panne", "Vérifier le dernier déploiement"]),
        502 or 503 or 504 => ($"{c.ServiceName} est indisponible derrière son proxy (HTTP {c.HttpStatusCode}).",
            ["Instances arrêtées ou en cours de redémarrage", "Maintenance ou déploiement en cours", "Répartiteur sans cible saine"],
            ["Vérifier le nombre d'instances saines", "Vérifier si un déploiement est en cours", "Lire les journaux du proxy ou du répartiteur"]),
        401 or 403 => ($"La route de santé de {c.ServiceName} exige une authentification (HTTP {c.HttpStatusCode}).",
            ["Route de santé protégée par erreur", "Règle de pare-feu applicatif modifiée"],
            ["Rendre la route de santé accessible sans authentification", "Vérifier les règles du pare-feu applicatif"]),
        404 => ($"La route de santé de {c.ServiceName} n'existe pas (HTTP 404).",
            ["Chemin de vérification modifié par un déploiement", "Mauvais chemin enregistré"],
            ["Vérifier le chemin enregistré dans DevPulse", "Vérifier les routes du dernier déploiement"]),
        429 => ($"{c.ServiceName} limite le débit des requêtes (HTTP 429).",
            ["Limite de débit trop basse pour la fréquence de vérification"],
            ["Exempter DevPulse de la limite, ou espacer les vérifications"]),
        { } code => ($"{c.ServiceName} répond avec un code inattendu (HTTP {code}).",
            ["Comportement applicatif modifié"],
            ["Comparer avec la réponse attendue de la route de santé"]),
        null => ($"{c.ServiceName} est en panne ({c.Failure}).",
            ["Cause non identifiée par les règles locales"],
            ["Consulter l'historique des vérifications et les journaux du service"]),
    };

    private static string Ms(IncidentContext c) => c.ResponseTimeMs is { } ms ? $" ({ms} ms)" : "";
}

/// <summary>Essaie le fournisseur configuré ; à la moindre erreur, se replie sur les règles. Un incident a toujours une analyse.</summary>
public sealed class AnalysisService(IAIAnalysisProvider provider, RuleBasedProvider rules, ILogger<AnalysisService> logger)
{
    public async Task<AnalysisResult> AnalyzeAsync(IncidentContext context, CancellationToken ct)
    {
        if (provider is RuleBasedProvider) return await rules.AnalyzeAsync(context, ct);

        try
        {
            return await provider.AnalyzeAsync(context, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Analyse par {Provider} impossible : repli sur les règles locales", provider.Name);
            return await rules.AnalyzeAsync(context, ct);
        }
    }
}

/// <summary>
/// Pas de base pour ce worker : une seconde livraison du même incident produit une seconde analyse, qu'Incident
/// ignore (une seule analyse par incident). Accepter ce doublon évite une base pour un traitement sans état.
/// </summary>
public sealed class IncidentCreatedHandler(AnalysisService analysis, IOutbox outbox, TimeProvider clock) : IIntegrationEventHandler<IncidentCreatedV1>
{
    public const string Producer = "ai-worker";

    public async Task HandleAsync(IncidentCreatedV1 e, CancellationToken ct)
    {
        var result = await analysis.AnalyzeAsync(IncidentContext.From(e), ct);
        outbox.Enqueue(new AnalysisCompletedV1
        {
            Producer = Producer,
            OrganizationId = e.OrganizationId,
            CorrelationId = e.CorrelationId,
            CausationId = e.EventId.ToString(),
            IncidentId = e.IncidentId,
            Provider = result.Provider,
            Model = result.Model,
            Summary = result.Summary,
            ProbableCauses = result.ProbableCauses,
            SuggestedActions = result.SuggestedActions,
            GeneratedAt = clock.GetUtcNow(),
        });
    }
}
