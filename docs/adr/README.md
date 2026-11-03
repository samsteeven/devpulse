# Décisions d'architecture

Les huit premières viennent du cahier des charges (Technical Blueprint v1.0). La colonne « État » dit ce qui existe réellement dans le code, pas ce qui est prévu.

| ADR | Décision | Conséquence à assumer | État |
|---|---|---|---|
| 001 | Microservices aux frontières métier, limités à 5 services et un worker IA | Complexité réseau, cohérence distribuée, coût | 3 services sur 5, passerelle |
| 002 | Une base par service, sans clé étrangère ni entité partagée | Données échangées par API ou événements | Fait, vérifié par des tests d'architecture |
| 003 | RabbitMQ en `topic` pour les événements, HTTP pour ce qui attend une réponse | Outbox, idempotence, reprises et file morte à écrire | RabbitMQ démarré, **aucun usage applicatif** |
| 004 | Passerelle YARP, sans règle métier | Point d'entrée unique à protéger | Fait |
| 005 | Monitoring hybride : sondes HTTP et agent | L'agent demande une identité, une révocation, des collecteurs par système | Sondes faites, **agent non commencé** |
| 006 | OpenTelemetry et Serilog | Un collecteur à exploiter | Fait en local, sans stockage |
| 007 | IA derrière `IAIAnalysisProvider`, sans action automatique | Données à filtrer avant l'envoi | Non commencé |
| 008 | ECS Fargate sur AWS, déployé avec Terraform | Coût des services managés | Non commencé |

## ADR-009 : RabbitMQ.Client plutôt que MassTransit

**État : proposé.** À confirmer au début de la phase 3, aucun code n'en dépend encore.

**Contexte.** Le cahier des charges suggère MassTransit, sans en faire une décision. Trois éléments changent la donne :

- La v9 est sous licence commerciale. La v8 reste libre, mais les sources trouvées ne garantissent ses correctifs que jusqu'à fin 2026 environ ; ce sont des sources secondaires, à revérifier sur le site de l'éditeur.
- Par défaut, MassTransit crée un échange par type de message. La topologie retenue (un échange `devpulse.events`, des clés de routage versionnées) lui demanderait une configuration contre-nature.
- L'outbox et l'idempotence sont justement ce que ce projet doit démontrer.

**Décision.** Utiliser `RabbitMQ.Client` 7 directement, avec une petite couche `DevPulse.Messaging` : confirmations d'éditeur, messages persistants, outbox, table de messages traités, reprises, file morte.

**Conséquences.** Plus de code à écrire et à tester, mais chaque garantie se défend en entretien. La reprise avec délai est à construire, par exemple avec des files d'attente à TTL.

**Alternatives.** MassTransit v8 : rapide à brancher, mais fin de maintenance proche. MassTransit v9 : licence payante. Wolverine : non évalué.

## ADR-010 : jetons signés en RS256

**État : accepté, implémenté.**

**Contexte.** Tous les services valident les jetons. Avec une clé symétrique (HS256), chacun pourrait aussi en fabriquer, pour n'importe quelle organisation.

**Décision.** Identity signe avec une clé privée RSA ; les autres services et la passerelle ne reçoivent que la clé publique. Seul l'algorithme RS256 est accepté à la validation.

**Conséquences.** Un service compromis ne peut pas forger de jeton. La rotation de clé reste à faire : il faudra publier plusieurs clés (JWKS) et remplacer la configuration statique.

## ADR-011 : l'isolation entre organisations est explicite dans chaque requête

**État : accepté, implémenté.**

**Décision.** Les dépôts prennent `organizationId` en paramètre obligatoire, et une ressource d'une autre organisation répond 404, pas 403, pour ne pas révéler son existence. La seule lecture transversale porte son nom (`ListEnabledAcrossOrganizationsAsync`) et n'est joignable que par la route interne.

**Conséquences.** La règle se voit à la lecture du code et casse à la compilation si on l'oublie. Elle ne protège pas d'une erreur dans la requête elle-même : des tests d'isolation le vérifient. Row Level Security pourrait ajouter une seconde barrière en base.

**Alternative.** Filtres globaux EF Core : moins de répétition, mais une règle invisible à la lecture et facile à désactiver par `IgnoreQueryFilters`.
