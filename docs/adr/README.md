# Décisions d'architecture

Les huit premières viennent du cahier des charges (Technical Blueprint v1.0). La colonne « État » dit ce qui existe réellement dans le code, pas ce qui est prévu.

| ADR | Décision | Conséquence à assumer | État |
|---|---|---|---|
| 001 | Microservices aux frontières métier, limités à 5 services et un worker IA | Complexité réseau, cohérence distribuée, coût | Fait : Identity, Registry, Monitoring, Incident, Alerting, worker IA, passerelle |
| 002 | Une base par service, sans clé étrangère ni entité partagée | Données échangées par API ou événements | Fait, vérifié par des tests d'architecture |
| 003 | RabbitMQ en `topic` pour les événements, HTTP pour ce qui attend une réponse | Outbox, idempotence, reprises et file morte à écrire | Fait ([ADR-009](#adr-009--rabbitmqclient-plutôt-que-masstransit)), testé contre un vrai RabbitMQ |
| 004 | Passerelle YARP, sans règle métier | Point d'entrée unique à protéger | Fait |
| 005 | Monitoring hybride : sondes HTTP et agent | L'agent demande une identité, une révocation, des collecteurs par système | Fait ([ADR-014](#adr-014--lagent-parle-à-monitoring-pas-au-collecteur-opentelemetry)) |
| 006 | OpenTelemetry et Serilog | Un collecteur à exploiter | Fait en local, sans stockage |
| 007 | IA derrière `IAIAnalysisProvider`, sans action automatique | Données à filtrer avant l'envoi | Fait ([ADR-015](#adr-015--deux-fournisseurs-danalyse-règles-locales-et-api-compatible-openai)) |
| 008 | ECS Fargate sur AWS, déployé avec Terraform | Coût des services managés | Terraform écrit et validé en CI, **jamais appliqué** |

## ADR-009 : RabbitMQ.Client plutôt que MassTransit

**État : accepté, implémenté** (`src/BuildingBlocks/DevPulse.Messaging`).

**Contexte.** Le cahier des charges suggère MassTransit, sans en faire une décision. La v9 est sous licence commerciale ; les sources trouvées ne garantissent les correctifs de la v8 que jusqu'à fin 2026 environ (sources secondaires). Par défaut, MassTransit crée un échange par type de message, alors que la topologie retenue est un échange `devpulse.events` et des clés de routage versionnées. Enfin, l'outbox et l'idempotence sont justement ce que ce projet doit démontrer.

**Décision.** `RabbitMQ.Client` 7 directement, avec une couche `DevPulse.Messaging` :

- **Outbox** : l'événement est écrit dans `outbox_messages` dans la transaction métier. Un distributeur le publie avec confirmation du courtier, puis le marque publié. Plusieurs instances se partagent la table (`FOR UPDATE SKIP LOCKED`).
- **Consommateur** : l'identifiant de l'événement est inscrit dans `processed_messages` dans la transaction du traitement ; un doublon est acquitté sans effet. L'acquittement suit la validation.
- **Reprises** : une file `<file>.retry` à durée de vie renvoie le message dans la file principale après un délai ; au-delà de `MaxAttempts`, file morte `<file>.dlq` avec la dernière erreur en en-tête.
- **Topologie** déclarée à l'identique par chaque service au démarrage : un événement publié avant le démarrage de son consommateur attend dans sa file. Publication `mandatory` : un événement qu'aucune file ne reçoit revient en erreur.
- Files **quorum**, messages persistants, contexte de trace W3C propagé dans les en-têtes.

**Conséquences.** Plus de code, mais chaque garantie est testée sur un vrai RabbitMQ (doublon sans effet, transaction annulée sans publication, reprise après délai, file morte, courtier injoignable). Livraison « au moins une fois », jamais « exactement une fois » : l'idempotence fait le reste. L'ordre n'est pas garanti : les consommateurs comparent les dates.

**Alternatives.** MassTransit v8 : rapide à brancher, maintenance proche de sa fin. v9 : licence payante. Wolverine : non évalué.

## ADR-010 : jetons signés en RS256

**État : accepté, implémenté.**

**Contexte.** Tous les services valident les jetons. Avec une clé symétrique (HS256), chacun pourrait aussi en fabriquer, pour n'importe quelle organisation.

**Décision.** Identity signe avec une clé privée RSA ; les autres services et la passerelle ne reçoivent que la clé publique. Seul l'algorithme RS256 est accepté à la validation.

**Conséquences.** Un service compromis ne peut pas forger de jeton. La rotation de clé reste à faire : il faudra publier plusieurs clés (JWKS) et remplacer la configuration statique.

## ADR-011 : l'isolation entre organisations est explicite dans chaque requête

**État : accepté, implémenté.**

**Décision.** Les dépôts prennent `organizationId` en paramètre obligatoire, et une ressource d'une autre organisation répond 404, pas 403, pour ne pas révéler son existence. Les lectures transversales portent leur nom (`ListEnabledAcrossOrganizationsAsync`, `FindByIdForEventAsync`) et ne sont joignables que par une route interne ou un événement, dont l'organisation vient de l'émetteur.

**Conséquences.** La règle se voit à la lecture du code et casse à la compilation si on l'oublie. Elle ne protège pas d'une erreur dans la requête elle-même : des tests d'isolation le vérifient, service par service, y compris pour le hub temps réel. Row Level Security pourrait ajouter une seconde barrière en base.

**Alternative.** Filtres globaux EF Core : moins de répétition, mais une règle invisible à la lecture et facile à désactiver par `IgnoreQueryFilters`.

## ADR-012 : Incident ouvre l'incident, Alerting ne fait que prévenir

**État : accepté, implémenté.**

**Contexte.** Le cahier des charges fait passer la panne par Alerting (`AlertTriggered → Incident`) ; le blueprint la fait consommer directement par Incident.

**Décision.** Incident consomme `monitoring.service.down-detected` et ouvre l'incident ; Alerting consomme les événements d'incident et applique ses règles de notification.

**Raison.** L'incident est un fait (le service est tombé), la notification est une politique (qui prévenir, pour quels environnements). Si Alerting créait l'incident, couper une règle d'alerte effacerait l'historique des pannes, et la durée moyenne de résolution deviendrait fausse.

**Conséquences.** Un incident existe même sans canal d'alerte. `AlertTriggered` disparaît du modèle : les envois sont des données internes d'Alerting (`alert_deliveries`), consultables par l'API.

## ADR-013 : le hub SignalR vit dans Alerting

**État : accepté, implémenté.**

**Contexte.** Le hub figure sur le diagramme du blueprint, pas dans l'arborescence.

**Décision.** Le temps réel est un canal de notification comme un autre : le hub `/hubs/notifications` est servi par Alerting, qui consomme déjà les événements d'incident et reçoit les changements d'état (`monitoring.service.status-changed`). Chaque connexion rejoint le groupe de l'organisation lue dans son jeton, sans méthode pour en rejoindre un autre. Redis sert de fond de panier quand plusieurs instances tournent.

**Conséquences.** Pas de sixième service. Les messages ne portent pas l'état : ils disent à l'interface quoi relire, qui garde une interrogation lente en secours. Le jeton passe dans l'URL pour la connexion WebSocket (les navigateurs ne permettent pas d'en-tête) : il n'est accepté ainsi que sous `/hubs`, et l'instrumentation OpenTelemetry masque la chaîne de requête.

## ADR-014 : l'agent parle à Monitoring, pas au collecteur OpenTelemetry

**État : accepté, implémenté.**

**Contexte.** Le blueprint fait passer les données de l'agent par le collecteur OpenTelemetry. Celui-ci n'a ni authentification par client, ni moyen d'associer une donnée à une organisation, ni exportateur vers Monitoring.

**Décision.** L'agent envoie un battement HTTPS à `POST /api/agents/ingest` avec sa propre clé (`Authorization: Agent dpa_…`). La clé est générée à l'enregistrement, montrée une fois, stockée par empreinte et révocable. L'organisation vient de la clé, jamais du contenu. Un agent silencieux trois battements produit les mêmes événements qu'un service en panne, avec la source `Agent`.

**Conséquences.** Incidents, alertes et analyse fonctionnent pour les agents sans code dédié. L'agent ne transmet que des mesures de la machine (processeur, mémoire, disque, durée de fonctionnement), pas de journaux ni de traces : il ne remplace pas un agent OpenTelemetry.

## ADR-015 : deux fournisseurs d'analyse, règles locales et API compatible OpenAI

**État : accepté, implémenté.**

**Décision.** `IAIAnalysisProvider` a deux implémentations : des règles locales (par défaut, sans clé ni réseau) et l'API « chat completions » compatible OpenAI, qui couvre OpenAI, Mistral, Groq ou un modèle local servi par Ollama. Toute erreur du modèle, ou une réponse qui n'est pas l'objet JSON attendu, fait retomber sur les règles.

**Données envoyées.** Nom du service (nettoyé et tronqué), environnement, type de panne, code HTTP, temps de réponse, nombre d'échecs. Jamais d'URL, de nom d'hôte ni d'identifiant d'organisation. La consigne système dit de traiter ces champs comme des données.

**Conséquences.** Un incident a toujours une analyse, affichée comme une piste à vérifier. Aucune action n'est déclenchée à partir de son contenu, ce qui limite l'effet d'une injection par le nom d'un service. Le worker n'a pas de base : une seconde livraison du même incident produit une seconde analyse, qu'Incident ignore.

## ADR-016 : jeton de renouvellement en cookie, jeton d'accès en mémoire

**État : accepté, implémenté.**

**Décision.** Le jeton d'accès (15 min) ne vit qu'en mémoire dans le navigateur. Le jeton de renouvellement (14 jours) est opaque, en cookie `HttpOnly`, `Secure`, `SameSite=Strict`, limité au chemin `/api/auth`, et stocké par empreinte. Chaque usage le remplace ; présenter un jeton déjà remplacé révoque toute sa famille. Les routes qui lisent ce cookie exigent un en-tête que seul un script de la même origine peut poser.

**Conséquences.** Un script injecté ne trouve aucun jeton dans le stockage du navigateur ; il peut encore agir pendant que la page est ouverte, ce qu'aucun stockage ne protège. Retirer un membre ou changer son rôle révoque ses sessions, mais un jeton d'accès déjà émis reste valable jusqu'à 15 minutes.
