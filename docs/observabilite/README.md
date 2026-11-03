# Supervision de DevPulse

DevPulse surveille les services de ses clients ; cette page dit comment DevPulse lui-même est surveillé. Tout part d'OpenTelemetry dans le code, puis va vers Grafana en local ou Application Insights sur Azure, sans changer une ligne.

## Ce qui est mesuré

Traces, métriques et journaux de chaque service, avec `service.name` pour les distinguer (`api-gateway`, `identity-service`, `registry-service`, `monitoring-api`, `monitoring-worker`, `incident-service`, `alerting-service`, `ai-worker`).

**Traces.** Requêtes HTTP, appels sortants, requêtes PostgreSQL, et la messagerie : publication depuis l'outbox puis traitement chez le consommateur, reliés par le contexte W3C propagé dans les messages. Une panne se suit de la vérification au webhook dans une seule trace. Les interrogations périodiques (réservation des cibles, outbox, envois) sont exclues des traces, pour ne pas les noyer.

**Métriques métier** (`DevPulse.*`, en plus de celles d'ASP.NET Core, de HttpClient et du runtime .NET) :

| Métrique | Type | Étiquettes | Ce qu'elle dit |
|---|---|---|---|
| `devpulse.checks` | compteur | `status`, `failure` | Vérifications faites. À zéro, plus rien n'est surveillé |
| `devpulse.check.duration` | histogramme (ms) | `status` | Temps de réponse des cibles |
| `devpulse.outages.detected` | compteur | `source` (Probe, Agent) | Pannes confirmées |
| `devpulse.agents.heartbeats` | compteur | | Battements d'agents acceptés |
| `devpulse.messaging.published` | compteur | `routing_key` | Événements confirmés par le courtier |
| `devpulse.messaging.publish.failures` | compteur | `routing_key` | Publications en échec (l'événement reste dans l'outbox) |
| `devpulse.messaging.consumed` | compteur | `queue`, `outcome` | processed, duplicate, retried, dead_lettered |
| `devpulse.messaging.processing.duration` | histogramme (ms) | `queue` | Durée de traitement d'un message |
| `devpulse.outbox.pending` | jauge | `store` | Événements écrits, pas encore publiés |
| `devpulse.outbox.oldest.age` | jauge (s) | `store` | Âge du plus ancien : grandit si le courtier est absent |
| `devpulse.incidents.opened` | compteur | `severity`, `source` | Incidents ouverts |
| `devpulse.incidents.resolved` | compteur | `automatic`, `severity` | Incidents résolus |
| `devpulse.incidents.time_to_resolve` | histogramme (s) | `automatic` | Durée jusqu'à la résolution (MTTR) |
| `devpulse.alerts.deliveries` | compteur | `kind`, `event`, `outcome` | Tentatives d'envoi : sent, retry, failed |
| `devpulse.alerts.notification.latency` | histogramme (ms) | `event` | Du fait signalé à la réception par le destinataire |

Dans Prometheus, les points deviennent des soulignés, et l'unité ou `_total` s'ajoute : `devpulse_checks_total`, `devpulse_alerts_notification_latency_milliseconds_bucket`, `devpulse_outbox_oldest_age_seconds`.

**Limites connues.** Les jauges de l'outbox sont relues toutes les 10 s, par chaque instance qui publie (l'API et le worker de Monitoring partagent leur base : leurs valeurs se recouvrent). `devpulse.incidents.opened` est compté avant la validation de la transaction : un traitement repris après un échec peut compter un incident deux fois.

## En local : Grafana

`docker compose up -d` démarre `grafana/otel-lgtm` (collecteur OpenTelemetry, Prometheus, Tempo, Loki et Grafana dans une image) à la place de l'ancien collecteur de débogage.

- **Grafana** : http://localhost:3000, tableau de bord « DevPulse : plateforme » à l'ouverture (vérifications, incidents et alertes, messagerie, services HTTP). Traces : Explore > Tempo ; journaux : Explore > Loki.
- **Données en mémoire** (`tmpfs`, 1 Go) : rien n'est écrit dans le disque virtuel de Docker, l'historique repart de zéro à chaque redémarrage.
- **Fichiers versionnés** dans [`infrastructure/observability`](../../infrastructure/observability) : le tableau de bord (`dashboards/devpulse.json`) et les règles (`alerting/devpulse-rules.yaml`). Une modification faite dans Grafana n'est pas enregistrée : éditer le fichier.
- Les services envoient leurs métriques toutes les 15 s (`OTEL_METRIC_EXPORT_INTERVAL`).

## Règles d'alerte

Les mêmes cinq règles existent dans Grafana (local) et dans Azure Monitor (Azure, voir [`infrastructure/terraform-azure`](../../infrastructure/terraform-azure/README.md)) :

| Règle | Condition | Pourquoi |
|---|---|---|
| Plus aucune vérification | aucune vérification en 3 min, alors qu'il y en avait | Le worker est arrêté ou bloqué : plus rien n'est surveillé, sans aucune erreur visible |
| Message en file morte | au moins un message en file morte en 10 min | Un incident, une alerte ou une analyse manque |
| Outbox en retard | plus vieil événement non publié > 60 s pendant 2 min | Courtier injoignable |
| Délai de notification au-dessus de 10 s | P95 sur 15 min > 10 s | L'objectif du cahier des charges n'est plus tenu |
| Alertes abandonnées | au moins un envoi abandonné en 15 min | Un destinataire n'a pas été prévenu |

En local, les règles sont chargées et évaluées, mais aucun point de contact n'est configuré : elles s'affichent dans Grafana (Alerting), sans prévenir personne.

## Ce qui est vérifié

| Élément | Vérifié | Pas vérifié |
|---|---|---|
| Métriques métier | Tests sur un vrai RabbitMQ : publication, traitement, doublon, reprises, file morte, jauge de l'outbox quand le courtier est injoignable | Les autres métriques n'ont pas de test unitaire dédié |
| Chaîne complète | En CI, après les parcours navigateur et la charge, contre les images : les 11 métriques attendues sont dans Prometheus, les 8 services ont des traces dans Tempo, Grafana a chargé le tableau de bord et les 5 règles | Qu'une règle se déclenche vraiment : aucune panne de la plateforme n'est simulée |
| Délai de notification | 26 ms mesurés par le test navigateur pour l'ouverture d'un incident ; P95 de 2,4 s calculé par Prometheus sur les deux envois du test (ouverture et résolution). Ce chiffre est interpolé entre les bornes 1 s et 2,5 s de l'histogramme : la valeur réelle est quelque part dans cet intervalle | Sur un vrai volume d'alertes |
| Application Insights | Le code compile avec l'exportateur Azure Monitor | **Aucun envoi réel** : pas de déploiement Azure |

Un défaut a été trouvé en écrivant le test de la jauge : quand le courtier était absent au démarrage, le distributeur attendait la connexion et ne mesurait jamais l'outbox, alors que c'est le cas où l'âge doit grandir. La mesure tourne maintenant dans sa propre boucle.
