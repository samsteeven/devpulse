# Intégrer DevPulse

Ce qu'un système extérieur reçoit de DevPulse (webhooks), et ce qu'il doit envoyer (agent). Vérifié par les tests cités ; rien de ce qui suit n'a été essayé avec un vrai Slack ou une vraie flotte de machines.

## Webhooks

Un canal se crée dans la page « Alertes » (rôle Propriétaire, Administrateur ou DevOps). Il choisit ses événements (incident ouvert, pris en charge, résolu) et ses environnements (aucun coché : tous). L'URL doit être en `https` et désigner une adresse publique : DevPulse refuse les adresses internes à l'enregistrement **et** à chaque connexion.

### Type « Webhook » : JSON signé

Requête `POST`, corps JSON :

```json
{
  "event": "incident.created",
  "incidentId": "0193f0c2-…",
  "serviceName": "Payment API",
  "environment": "Production",
  "severity": "Critical",
  "title": "Payment API ne répond plus",
  "occurredAt": "2026-11-03T10:59:21.412Z",
  "detail": "UnexpectedStatus, HTTP 503"
}
```

| Champ | Valeurs |
|---|---|
| `event` | `incident.created`, `incident.acknowledged`, `incident.resolved` |
| `severity` | `Critical` (Production), `Major` (Staging), `Minor` (Development) |
| `occurredAt` | Moment du fait signalé : panne confirmée, prise en charge ou résolution |
| `detail` | Cause constatée à l'ouverture ; à la résolution, « retour automatique » ou « clos manuellement » et la durée ; `null` à la prise en charge |

En-têtes :

| En-tête | Contenu |
|---|---|
| `X-DevPulse-Event` | `IncidentCreated`, `IncidentAcknowledged`, `IncidentResolved` |
| `X-DevPulse-Delivery` | Identifiant de l'envoi, identique d'une tentative à l'autre : de quoi ignorer un doublon |
| `X-DevPulse-Timestamp` | Secondes Unix au moment de l'envoi |
| `X-DevPulse-Signature` | `sha256=` suivi du HMAC-SHA256, en hexadécimal minuscule, de `<timestamp>.<corps brut>` avec la clé du canal |

La clé de signature (64 caractères hexadécimaux) n'est affichée qu'une fois, à la création du canal.

**Vérifier un envoi** (Node.js) :

```js
import { createHmac, timingSafeEqual } from 'node:crypto'

function isFromDevPulse(rawBody, headers, secret) {
  const timestamp = headers['x-devpulse-timestamp']
  if (Math.abs(Date.now() / 1000 - Number(timestamp)) > 300) return false // rejoué ou trop ancien
  const expected = 'sha256=' + createHmac('sha256', secret).update(`${timestamp}.${rawBody}`).digest('hex')
  const received = headers['x-devpulse-signature'] ?? ''
  return received.length === expected.length && timingSafeEqual(Buffer.from(received), Buffer.from(expected))
}
```

Le calcul porte sur le **corps brut**, tel que reçu : un corps relu puis resérialisé (espaces, ordre des champs) ne donnerait pas la même signature. Le test navigateur `incident-journey.spec.ts` recalcule cette signature côté destinataire.

### Type « Slack »

Requête `POST` au format des webhooks entrants Slack, `{"text": "…"}`, non signée : Slack authentifie par le secret contenu dans l'URL. Mattermost et Rocket.Chat acceptent le même format.

### Livraison

- Un code 2xx vaut réception. Sinon, nouvelle tentative après 10 s, 30 s, 2 min, puis 10 min ; après la cinquième, l'envoi est abandonné. L'historique et le délai de chaque envoi sont dans la page « Alertes ».
- Chaque tentative a 10 s pour aboutir. Les redirections ne sont pas suivies.
- L'ordre d'arrivée n'est pas garanti : se fier à `occurredAt`.
- Le bouton « Envoyer un essai » envoie tout de suite un message signé de la même façon, sans reprise.

## Agent

L'agent surveille une machine que les sondes de DevPulse ne peuvent pas joindre (réseau privé). Il envoie toutes les 30 s un battement avec la charge du processeur, la mémoire, le disque système et la durée de fonctionnement. Trois battements manqués (une minute au minimum) ouvrent un incident ; le battement suivant le ferme.

### Installation

1. Dans la page « Agents », créer un agent. La clé `dpa_…` n'est affichée qu'une fois.
2. Sur la machine, avec le runtime .NET 10 :

   ```bash
   dotnet publish src/Agent/DevPulse.Agent -c Release -o /opt/devpulse-agent
   DevPulse__Url=https://devpulse.example.com DevPulse__AgentKey=dpa_… dotnet /opt/devpulse-agent/DevPulse.Agent.dll
   ```

   Sous Linux, un service systemd convient (`Restart=always`, variables dans `Environment=` ou un fichier lisible par le seul compte de service). Sous Windows, l'agent tourne en console ou en tâche planifiée : il n'est **pas** empaqueté en service Windows (il faudrait `UseWindowsService`), `sc.exe create` ne suffirait pas.

| Variable | Défaut | Sens |
|---|---|---|
| `DevPulse__Url` | aucun | Adresse de DevPulse, en `https` |
| `DevPulse__AgentKey` | aucune | Clé affichée à la création |
| `DevPulse__IntervalSeconds` | 30 | De 10 à 3600 |
| `DevPulse__AllowInsecureHttp` | `false` | `true` seulement en local : la clé passerait en clair |

### Comportement

- La clé est la seule identité de l'agent : l'organisation est celle de la clé, jamais une donnée envoyée. Révoquer la clé dans la page « Agents » coupe l'agent ; il reçoit alors 401 et s'arrête de lui-même.
- Un battement perdu n'est pas renvoyé : le suivant suffit.
- Mesures : `/proc/stat`, `/proc/meminfo` et `/proc/uptime` sous Linux ; `GetSystemTimes` et `GlobalMemoryStatusEx` sous Windows. Testées sous Windows (poste) et Linux (CI). Pas de macOS.

### Envoyer des battements sans l'agent

N'importe quel programme peut jouer ce rôle :

```bash
curl -X POST https://devpulse.example.com/api/agents/ingest \
  -H "Authorization: Agent dpa_…" -H "Content-Type: application/json" \
  -d '{"hostname":"srv-01","operatingSystem":"Linux","agentVersion":"1.0.0","intervalSeconds":30,
       "cpuPercent":12.5,"memoryPercent":40,"diskPercent":70,"uptimeSeconds":3600}'
```

Réponse 202 si le battement est accepté, 401 pour une clé fausse ou révoquée (sans distinction), 400 pour une valeur hors bornes (pourcentages de 0 à 100, intervalle de 10 à 3600 s).

## API

Chaque API publie sa description OpenAPI en environnement `Development` : `http://localhost:5010/swagger` (Identity), `5011` (Registry), `5012` (Monitoring), `5013` (Incident), `5014` (Alerting). En dehors du développement, tout passe par la passerelle (`/api/...`), avec un jeton d'accès obtenu à la connexion.
