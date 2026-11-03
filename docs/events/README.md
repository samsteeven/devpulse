# Événements

**État : contrats écrits et testés (`DevPulse.Contracts`) pour Monitoring. Aucun événement n'est encore publié ni consommé.** Cette page fixe la cible de la phase 3.

## Conventions

- Échange principal `devpulse.events` (type `topic`), échange de rebut `devpulse.dlx`.
- Clé de routage : `{domain}.{entity}.{event}.v{version}`, par exemple `monitoring.service.down-detected.v1`.
- Enveloppe à plat, sans objet `data` : `eventId`, `eventType`, `version`, `occurredAt`, `producer`, `correlationId`, `causationId`, `organizationId`, puis les champs de l'événement.

## Files et liaisons

Une file par consommateur, chacune avec sa file morte `<file>.dlq`.

| File | Liaisons |
|---|---|
| `incident.service-state` | `monitoring.service.down-detected.v1`, `monitoring.service.recovered.v1` |
| `alerting.service-state` | `monitoring.service.down-detected.v1`, `monitoring.service.recovered.v1` |
| `alerting.incident-events` | `incident.incident.*.v1` |
| `ai.incident-created` | `incident.incident.created.v1` |
| `incident.ai-analysis` | `ai.analysis.completed.v1` |

**Pourquoi pas `monitoring.service.*` :** en AMQP, `*` remplace exactement un mot. Ce motif ne correspond pas à une clé en quatre segments, et la file resterait vide sans aucune erreur. **Pourquoi pas `monitoring.service.#` :** il correspondrait, mais ferait passer `check-completed`, émis à chaque vérification, dans des files qui ne s'intéressent qu'aux changements d'état. D'où des liaisons explicites. Le test `EventContractTests` fixe cette sémantique.

## Garanties à écrire

- **Éditeur :** outbox dans la transaction métier, confirmations, messages persistants.
- **Consommateur :** identifiant de l'événement inscrit dans `ProcessedMessages` dans la même transaction que l'effet métier ; acquittement seulement après validation ; reprises avec délai, puis file morte.
- **Ordre :** RabbitMQ ne le garantit pas entre plusieurs consommateurs. `recovered` peut arriver avant `down-detected` : les consommateurs devront comparer les dates.
