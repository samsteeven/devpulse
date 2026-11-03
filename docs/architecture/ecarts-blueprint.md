# Écarts avec le Technical Blueprint v1.0

Le blueprint reste la référence. Cette page liste ce qui y a été corrigé en le mettant en œuvre, et comment ses questions ouvertes ont été tranchées.

## Corrigé

| Blueprint | Problème constaté | Retenu |
|---|---|---|
| Liaisons `monitoring.service.*`, `incident.incident.*` | `*` vaut un seul mot : avec des clés en quatre segments, ces files ne reçoivent rien | Liaisons explicites ([événements](../events/README.md)) |
| Healthcheck Redis avec `$${REDIS_PASSWORD}` | Variable absente de l'environnement du conteneur : Redis restait `unhealthy` | `REDISCLI_AUTH` dans l'environnement ; mot de passe hors de la commande |
| `pg_isready -U devpulse` | Utilisateur écrit en dur alors que `POSTGRES_USER` est configurable ; `pg_isready` répond « prêt » pendant que le script d'initialisation tourne | `psql` sur la dernière base créée |
| Images `rabbitmq:4-management`, `postgres:17-alpine`, `redis:7-alpine`, otel `0.120.0` | Le blueprint demande lui-même de les figer | `4.3.7-management-alpine`, `17.11-alpine`, `7.4.12-alpine`, `0.164.0` |
| Port PostgreSQL 5432 | Refusé par Windows sur ce poste | Variable `POSTGRES_PORT`, 5433 par défaut |
| `IntegrationEvent` en C# | Sans `eventType`, `version` ni `producer`, alors que le JSON les exige ; `OrganizationId` en texte malgré la convention UUID ; JSON avec un objet `data`, C# à plat | Tous les champs présents, `Guid`, enveloppe à plat |
| MassTransit (suggéré) | Licence de la v9, fin de maintenance de la v8, topologie par défaut différente | `RabbitMQ.Client` ([ADR-009](../adr/README.md)) |
| Jetons JWT (algorithme non précisé) | HS256 permettrait à chaque service de forger des jetons | RS256 ([ADR-010](../adr/README.md)) |
| `ServiceCheckCompleted` | Une vérification par minute et par service, sans consommateur | Non publié ; `status-changed` pour le temps réel |
| `AlertTriggered → Incident` | Couper une alerte effacerait l'historique des pannes | Incident consomme la panne ([ADR-012](../adr/README.md)) |
| Agent → collecteur OpenTelemetry | Ni authentification par client, ni rattachement à une organisation | Battement HTTPS vers Monitoring avec une clé d'agent ([ADR-014](../adr/README.md)) |

## Questions tranchées

| Question | Décision |
|---|---|
| Qui crée l'incident ? | Incident, sur `down-detected` ([ADR-012](../adr/README.md)) |
| Où vit le hub SignalR ? | Dans Alerting ([ADR-013](../adr/README.md)) |
| Qui consomme `ServiceCheckCompleted` et `AlertTriggered` ? | Personne : ni l'un ni l'autre n'est publié |
| Comment les données de l'agent arrivent-elles dans Monitoring ? | `POST /api/agents/ingest`, clé propre à l'agent ([ADR-014](../adr/README.md)) |
| Coût AWS | Estimé dans [cout-aws.md](cout-aws.md) ; aucun déploiement réel |

## Détail de nommage

Le blueprint parle de `TelemetryDb` et le script de `devpulse_monitoring` : c'est la même base. Le script fait foi.
