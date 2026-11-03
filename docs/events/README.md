# Événements

**État : publiés et consommés.** Les contrats sont dans `DevPulse.Contracts`, la topologie dans `EventTopology` ; la couche `DevPulse.Messaging` est décrite dans l'[ADR-009](../adr/README.md#adr-009--rabbitmqclient-plutôt-que-masstransit).

## Conventions

- Échange principal `devpulse.events` (type `topic`).
- Clé de routage : `{domain}.{entity}.{event}.v{version}`, par exemple `monitoring.service.down-detected.v1`.
- Enveloppe à plat, sans objet `data` : `eventId`, `eventType`, `version`, `occurredAt`, `producer`, `correlationId`, `causationId`, `organizationId`, puis les champs de l'événement.
- En-têtes AMQP : `x-routing-key` (clé d'origine, conservée lors des reprises), `x-attempt`, `x-last-error` (file morte), `traceparent`.

## Événements

| Clé | Émetteur | Sens |
|---|---|---|
| `monitoring.service.status-changed.v1` | Monitoring | L'état affiché change (Opérationnel, Dégradé, En panne). Sert au temps réel, n'ouvre rien |
| `monitoring.service.down-detected.v1` | Monitoring | Panne confirmée : deux échecs consécutifs, ou agent silencieux (source `Agent`) |
| `monitoring.service.recovered.v1` | Monitoring | Retour après une panne annoncée, avec la date de début de la panne |
| `incident.incident.created.v1` | Incident | Incident ouvert |
| `incident.incident.acknowledged.v1` | Incident | Pris en charge par un membre |
| `incident.incident.resolved.v1` | Incident | Résolu, automatiquement ou à la main, avec sa durée |
| `incident.incident.analyzed.v1` | Incident | Analyse rattachée (rafraîchissement de l'interface) |
| `ai.analysis.completed.v1` | Worker IA | Proposition d'analyse |

`monitoring.service.check-completed`, présent dans le blueprint, n'est pas publié : une vérification par minute et par service serait l'essentiel du trafic, sans consommateur. L'analytique reste dans Monitoring, qui lit sa propre base.

## Files et liaisons

Une file par consommateur, avec sa file de reprise `<file>.retry` et sa file morte `<file>.dlq`. Toutes sont des files quorum.

| File | Consommateur | Liaisons |
|---|---|---|
| `incident.service-state` | Incident | `monitoring.service.down-detected.v1`, `monitoring.service.recovered.v1` |
| `incident.ai-analysis` | Incident | `ai.analysis.completed.v1` |
| `ai.incident-created` | Worker IA | `incident.incident.created.v1` |
| `alerting.incident-events` | Alerting | `incident.incident.*.v1` |
| `alerting.service-status` | Alerting | `monitoring.service.status-changed.v1` |

**Pourquoi pas `monitoring.service.*` :** en AMQP, `*` remplace exactement un mot. Ce motif ne correspond pas à une clé en quatre segments, et la file resterait vide sans aucune erreur. **Pourquoi pas `monitoring.service.#` :** il ferait passer `status-changed`, émis à chaque changement d'affichage, dans la file d'Incident. D'où des liaisons explicites. `incident.incident.*.v1` est correct : `*` y remplace le seul mot variable. Les tests `EventContractTests` et `TopologyTests` fixent cette sémantique, et un traitement déclaré pour un événement non lié à sa file empêche le service de démarrer.

## Garanties

| Garantie | Où | Vérifié par |
|---|---|---|
| Pas d'événement pour un changement annulé | Outbox dans la transaction métier | `Une_transaction_annulee_ne_publie_rien` |
| Publication confirmée par le courtier | Canal avec confirmations suivies | Publication réelle dans les tests |
| Événements gardés si le courtier est absent | L'outbox attend, reprise avec délai croissant | `Le_distributeur_garde_les_evenements_tant_que_le_courtier_est_injoignable` |
| Doublon sans effet | `processed_messages` dans la transaction du traitement | `Un_doublon_du_meme_evenement_n_a_aucun_effet` (et casse volontaire) |
| Reprise après délai, puis file morte | Files `.retry` à durée de vie, `.dlq` | `Un_echec_passager_…`, `Un_echec_persistant_…` |
| Ordre | **Non garanti** | Incident ignore une panne antérieure à la dernière reprise connue (`ServiceOutageState`) |

Le worker IA n'a pas de base : il publie après son traitement, avant l'acquittement. Un arrêt entre les deux produit une seconde analyse, qu'Incident ignore (une analyse par incident).
