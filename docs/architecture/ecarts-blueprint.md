# Écarts avec le Technical Blueprint v1.0

Le blueprint reste la référence. Cette page liste ce qui y a été corrigé en le mettant en œuvre, et ce qu'il laisse ouvert.

## Corrigé

| Blueprint | Problème constaté | Retenu |
|---|---|---|
| Liaisons `monitoring.service.*`, `incident.incident.*` | `*` vaut un seul mot : avec des clés en quatre segments, ces files ne reçoivent rien | Liaisons explicites ([événements](../events/README.md)) |
| Healthcheck Redis avec `$${REDIS_PASSWORD}` | Variable absente de l'environnement du conteneur : Redis restait `unhealthy` | `REDISCLI_AUTH` dans l'environnement ; mot de passe hors de la commande |
| `pg_isready -U devpulse` | Utilisateur écrit en dur alors que `POSTGRES_USER` est configurable ; `pg_isready` répond « prêt » pendant que le script d'initialisation tourne | `psql` sur la dernière base créée |
| Images `rabbitmq:4-management`, `postgres:17-alpine`, `redis:7-alpine`, otel `0.120.0` | Le blueprint demande lui-même de les figer | `4.3.7-management-alpine`, `17.11-alpine`, `7.4.12-alpine`, `0.164.0` |
| Port PostgreSQL 5432 | Refusé par Windows sur ce poste | Variable `POSTGRES_PORT`, 5433 par défaut |
| `IntegrationEvent` en C# | Sans `eventType`, `version` ni `producer`, alors que le JSON les exige ; `OrganizationId` en texte malgré la convention UUID ; JSON avec un objet `data`, C# à plat | Tous les champs présents, `Guid`, enveloppe à plat |
| MassTransit (suggéré) | Licence de la v9, fin de maintenance de la v8, topologie par défaut différente | `RabbitMQ.Client`, proposé dans [ADR-009](../adr/README.md) |
| Jetons JWT (algorithme non précisé) | HS256 permettrait à chaque service de forger des jetons | RS256, [ADR-010](../adr/README.md) |

## Encore ouvert

À trancher avant la phase indiquée.

| Question | Détail | Phase |
|---|---|---|
| Qui crée l'incident ? | Le cahier des charges montre `Alerting → AlertTriggered → Incident` ; le blueprint fait consommer `ServiceDownDetected` directement par Incident. Le blueprint, plus récent, est suivi dans la topologie | 4 |
| Où vit le hub SignalR ? | Présent sur le diagramme, absent de l'arborescence | 4 |
| Qui consomme `ServiceCheckCompleted` et `AlertTriggered` ? | « Analytics » et « Notification Worker » ne figurent ni dans l'arborescence ni dans les files. L'analytique est aujourd'hui dans Monitoring ; sans consommateur, cet événement est à retirer | 3 et 4 |
| Comment les données de l'agent arrivent-elles dans Monitoring ? | Le collecteur n'a ni authentification ni exportateur vers Monitoring, et rien n'associe une donnée reçue à une organisation. L'identité de l'agent est à concevoir avant son code | Agent |
| Coût AWS | ECS, RDS, Amazon MQ, ElastiCache, NAT et ALB ont un coût fixe, même sans trafic : à chiffrer avant la phase 6, pour décider d'une démonstration temporaire | 6 |

## Détail de nommage

Le blueprint parle de `TelemetryDb` et le script de `devpulse_monitoring` : c'est la même base. Le script fait foi.
