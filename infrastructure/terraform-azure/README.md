# Déploiement Azure : Container Apps avec Terraform

Variante Azure du déploiement AWS ([`infrastructure/terraform`](../terraform/README.md)), pensée pour un abonnement **Azure for Students** (environ 100 $ de crédit, sans carte bancaire) : tout ce qui peut s'arrêter s'arrête, les journaux sont plafonnés et un budget prévient par courriel.

**Ce code n'a jamais été appliqué.** Personne n'a lancé `terraform plan`, encore moins `apply`. La CI exécute `fmt -check`, `init -backend=false` et `validate`, qui passent : cela vérifie la syntaxe, les types et les références, pas le comportement chez Azure.

## Ce qui est vérifié, et ce qui ne l'est pas

| Élément | Vérifié | Pas vérifié |
|---|---|---|
| Noms des variables d'environnement | Relus dans `docker-compose.yml` (profil `apps`) et dans le code (`RabbitMqOptions`, `ForwardedHeaders` de la passerelle, `Auth:SecureCookie`, `InternalApi`, `Redis:ConnectionString`, `Registry:BaseUrl`) | Que chaque service démarre avec exactement cette configuration |
| Clés JWT | Même dérivation PEM vers base64 DER que la version AWS (rejouée en .NET), même bloc `check` | Sur une clé produite par le fournisseur `tls` lui-même |
| Arguments des ressources | Relus le 3/11/2026 dans la documentation d'`hashicorp/azurerm` 5.x (branche principale) et dans son guide de migration 5.0 : `container_app`, `container_app_environment`, `postgresql_flexible_server` et `_database`, `subnet`, `private_dns_zone_virtual_network_link`, `log_analytics_workspace`, `consumption_budget_subscription`, `managed_redis`, bloc `provider` | Un vrai `plan` (valeurs refusées seulement par l'API Azure) |
| Comportement de Container Apps | Microsoft Learn : appel par `http://<application>`, redirection HTTP vers HTTPS si `allowInsecure` est faux, entrée TCP interne joignable par `<application>:<port exposé>` et possible **sans** réseau virtuel (seule l'entrée TCP externe en exige un), sous-réseau /27 délégué pour un environnement à profils de charge de travail, couples CPU/mémoire, WebSocket pris en charge | Rien n'a tourné : ni nginx vers `http://gateway`, ni YARP vers les API, ni le WebSocket `/hubs` à travers deux proxys |
| PostgreSQL 17 | Disponible sur Flexible Server (17.11, Microsoft Learn du 26/08/2026) ; `version = "17"` accepté par azurerm 5.x | Création réelle, disponibilité du B1ms dans la région autorisée de l'abonnement |
| Budget | Arguments relus | Qu'Azure l'accepte sur Azure for Students : la page « Understand Cost Management data » range encore cette offre (MS-AZR-0170P) parmi les non prises en charge, la page Education Hub conseille d'y créer des budgets |
| Supervision (arguments) | Relus le 3/11/2026 dans la documentation d'`hashicorp/azurerm` (branche principale) : `application_insights` (`workspace_id`, `daily_data_cap_in_gb` à 100 par défaut, `daily_data_cap_notifications_enabled`, `sampling_percentage`, `retention_in_days`, `local_authentication_enabled` ; les variantes en `_disabled` sont retirées en 5.0), `monitor_action_group`, `monitor_scheduled_query_rules_alert_v2`, blocs `features` `application_insights` et `resource_group` | Que la branche principale décrive exactement la 5.x installée par `init` ; un vrai `plan` |
| Supervision (comportement) | Microsoft Learn : plafond effectif = le plus petit de celui d'Application Insights et de celui de l'espace ; métriques jamais échantillonnées ; histogramme réduit à min, max, somme, nombre et moyenne, sans percentile ; correspondance `customMetrics` / `AppMetrics` et noms des colonnes ; tables `App*` gardées 90 jours sans frais | Ce que l'exportateur écrit vraiment (une ligne par série et par export, valeur des jauges dans `Sum` ; seuls l'export en delta toutes les 60 s et l'échantillonneur des traces ont été lus dans son code source) ; le déclenchement des règles ; le volume ingéré par jour (voir « Supervision ») |
| Coût | Prix unitaires lus dans l'API Azure Retail Prices : [`docs/architecture/cout-azure.md`](../../docs/architecture/cout-azure.md) | Une facture réelle |
| Tout le reste | Rien | Tout |

## Ce qui est créé

```
 Internet ─HTTPS─► web (nginx, SPA) ─ /api/, /hubs/ ─► http://gateway ─► http://identity, registry, monitoring-api,
                   entrée externe                      (YARP, interne)     incident, alerting      (entrées internes)
 monitoring-worker ─► http://registry ; sondes vers Internet        ai-worker ─► RabbitMQ (et le LLM si Provider = Llm)
 API et workers ─► rabbitmq:5672 (conteneur, entrée TCP interne) ; PostgreSQL 17 (sous-réseau délégué, TLS)
```

| Fichier | Contenu |
|---|---|
| `main.tf` | Groupe de ressources, Log Analytics (plafond quotidien), environnement Container Apps (profil Consumption), réplicas par défaut |
| `network.tf` | Réseau virtuel, sous-réseau de l'environnement (délégué à `Microsoft.App/environments`), sous-réseau délégué à PostgreSQL, zone DNS privée `<nom>.private.postgres.database.azure.com` |
| `database.tf` | PostgreSQL Flexible Server (B1ms, 32 Go, 7 jours de sauvegarde), les cinq bases, chaînes de connexion |
| `messaging.tf` | Conteneur RabbitMQ ou courtier externe ; Azure Managed Redis facultatif |
| `apps.tf` | Les neuf applications : variables, secrets, entrées, sondes, réplicas |
| `secrets.tf` | Mots de passe aléatoires, paire de clés JWT, valeurs des secrets Container Apps |
| `observability.tf` | Application Insights rattaché à l'espace Log Analytics (plafond quotidien propre), groupe d'actions par courriel, cinq règles d'alerte sur les métriques de la plateforme |
| `budget.tf` | Budget mensuel de l'abonnement, alertes à 50, 80, 100 % constatés et 100 % prévus |

## Choix et simplifications

- **Environnement à profils de charge de travail, profil Consumption seul.** Facturation à la seconde, offre gratuite mensuelle, pas de frais de gestion (ils ne s'appliquent qu'aux profils dédiés, points de terminaison privés et maintenance planifiée). Environnement externe : web est public.
- **Réseau virtuel (`enable_vnet`, vrai par défaut).** Il ne sert qu'à PostgreSQL : accès privé par sous-réseau délégué, aucun point d'accès public. Il coûte environ 25 $/mois, car un environnement posé dans son propre réseau fait facturer un répartiteur standard et deux IP publiques. Avec `enable_vnet = false`, PostgreSQL est public, limité par la règle « services Azure » (0.0.0.0) : toute adresse Azure, y compris d'autres clients, peut tenter de s'y connecter ; restent le mot de passe de 32 caractères et TLS obligatoire. Acceptable pour une démonstration courte, à choisir en connaissance de cause.
- **Adressage interne.** Les applications s'appellent par leur nom court (`http://gateway`, `http://registry/`…), sur le port 80 du proxy de l'environnement, qui renvoie vers 8080 et choisit l'application d'après l'en-tête `Host` (d'où l'en-tête `Host` de la passerelle dans `nginx.conf.template`). Les entrées internes acceptent HTTP (`allow_insecure_connections`) : sinon le proxy répondrait par une redirection vers HTTPS. Le trafic ne quitte pas l'environnement. Les noms des applications ne portent donc pas de préfixe.
- **Réplicas** (variable `replicas`, fusionnée avec les valeurs par défaut) :

  | Application | min | max | Pourquoi |
  |---|---:|---:|---|
  | web, gateway, identity, monitoring-api | 0 | 1 | Arrêt après 5 minutes sans requête ; démarrage à froid à la visite suivante (web, puis gateway, puis l'API : quelques dizaines de secondes, non mesuré) |
  | registry | 1 | 1 | monitoring-worker l'appelle toutes les 30 s : à 0 il ne s'arrêterait jamais et serait facturé au tarif actif ; à 1 il peut l'être au tarif inactif |
  | incident, alerting | 1 | 1 | Consomment des files RabbitMQ : rien ne les réveillerait. Un seul alerting tant qu'il n'y a pas de Redis |
  | monitoring-worker, ai-worker | 1 | 1 | Pas d'entrée HTTP, donc rien pour les réveiller |
  | rabbitmq | 1 | 1 | Fixe |

  Une API arrêtée ne publie plus son outbox, mais ses événements restent en base : ils partent au réveil suivant. En pratique, monitoring-api devrait n'écrire dans son outbox que pendant une requête, et il reste ensuite démarré 5 minutes, bien plus que l'intervalle d'envoi (1 s) : la perte de délai devrait être rare (non vérifié). incident et monitoring-worker, qui ont aussi une outbox, ne s'arrêtent pas. Étape suivante possible : une règle d'échelle KEDA `rabbitmq` sur ai-worker, pour qu'il ne tourne que s'il y a des messages.
- **RabbitMQ en conteneur, stockage éphémère.** Azure n'a pas de RabbitMQ managé. Le conteneur `rabbitmq:4.3.7-management-alpine` (étiquette de `docker-compose.yml`) a une entrée TCP interne sur 5672, un réplica. Un redémarrage (nouvelle révision, maintenance, plantage) **perd les messages déjà en file** : l'outbox ne protège que les événements pas encore confirmés par le courtier. Acceptable pour une démonstration, pas pour une production (il faudrait un volume Azure Files, ou un courtier managé). 0,5 Gio de mémoire suffit pour le trafic d'une démonstration, sans marge ; `app_resources` permet d'augmenter.
- **Courtier externe** (`external_rabbitmq_host`, `_port`, `_user`, `_password`, `_use_tls`, `_vhost`) : aucun conteneur, tous les services pointent vers lui. CloudAMQP, offre gratuite Little Lemur (lue le 3/11/2026) : 20 connexions, 100 files, 10 000 messages en file, **1 million de messages par mois**, offres partagées disponibles en West Europe et Sweden Central notamment. DevPulse déclare 15 files (5 files consommées, chacune avec `.retry` et `.dlq`) et ouvre une connexion par service qui publie ou consomme (5) : ça tient. Deux réserves : le quota mensuel de messages dépend du nombre de cibles surveillées, et les files sont déclarées en `quorum`, ce que la page des offres ne garantit pas pour un serveur partagé (non testé). Sur CloudAMQP, l'hôte virtuel est le nom d'utilisateur.
- **Redis désactivé** (`enable_redis`). Un seul réplica d'alerting n'a pas besoin de fond de panier SignalR. Azure Cache for Redis Basic C0 n'est plus une option : ses offres Basic, Standard et Premium sont retirées le 30 septembre 2028 (FAQ Microsoft Learn) et, d'après l'annonce du blog Tech Community (citée par cette FAQ, pas relue directement), la création de nouveaux caches est bloquée depuis le 1er octobre 2026. Activé, c'est un Azure Managed Redis Balanced_B0 sans haute disponibilité, accès public par clé et TLS. Un `precondition` refuse plus d'un réplica d'alerting sans Redis.
- **Une instance PostgreSQL, un utilisateur.** Les bases sont créées par Terraform ; chaque API migre la sienne au démarrage (`Database__MigrateOnStartup=true`), monitoring-worker non. Étape suivante : un utilisateur par service, limité à sa base. `SSL Mode=Require` chiffre sans vérifier le certificat.
- **Secrets Container Apps, pas de Key Vault**, pour rester au plus bas coût. Chaque application ne reçoit que les secrets que ses variables référencent ; identity, registry et la passerelle ne reçoivent pas le mot de passe RabbitMQ (ils n'utilisent pas le courtier). Étape de production : Key Vault, et une identité managée par application (`key_vault_secret_id`). Les secrets sont dans l'état Terraform en clair : exemple de backend chiffré dans `versions.tf`.
- **Limitation de débit de la passerelle.** Tout le trafic passe par le nginx de web avant la passerelle ; Container Apps ne garantit que l'adresse la plus à droite de `X-Forwarded-For`. La passerelle verra donc très probablement l'adresse de web pour tous les visiteurs : la limite `auth` (20 connexions par minute, `auth_permit_per_minute`) devient globale. Sans réseau virtuel, aucune plage de confiance n'est déclarée et l'en-tête est ignoré (même effet). Non vérifié.
- **Journaux.** Sortie standard des conteneurs vers Log Analytics (30 jours). Plafond `daily_quota_gb` = 0,15 Go par jour, soit au plus 4,65 Go par mois, sous les 5 Go gratuits ; au-delà, les journaux du jour sont perdus. Ce plafond compte aussi Application Insights, rangé dans le même espace. Pas de collecteur OpenTelemetry : `OTEL_EXPORTER_OTLP_ENDPOINT` n'est pas posé, les services exportent directement vers Application Insights (voir « Supervision »).
- **Sondes.** Démarrage puis vie sur `/health/live`, disponibilité sur `/health/ready` pour les cinq API ; `/health/live` pour la passerelle ; `/` pour web ; TCP 5672 pour RabbitMQ ; aucune pour les workers. La sonde de démarrage laisse 2 min 30 aux migrations.
- `TargetPolicy__AllowPrivateTargets=false` et `Auth__SecureCookie=true` sont posés sur tous les services .NET, comme l'ancre `x-app-env` de `docker-compose.yml` (l'entrée de Container Apps est en HTTPS).

## Avant de déployer sur Azure for Students

### Trouver les régions autorisées

D'après des réponses de la communauté Microsoft Q&A (pas une documentation officielle), une stratégie Azure Policy « Allowed resource deployment regions » limite ces abonnements à environ cinq régions, différentes d'un abonnement à l'autre. Une région refusée donne l'erreur `RequestDisallowedByAzure`. Pour les lire :

- Portail : **Policy** > **Assignments** > « Allowed resource deployment regions » > onglet **Parameters** ;
- ou en ligne de commande :

  ```bash
  az policy assignment list --query "[?displayName=='Allowed resource deployment regions'].parameters" -o json
  ```

Choisir dans cette liste une région européenne (`francecentral`, `westeurope`, `swedencentral`…) et la passer dans `location` (pas de valeur par défaut). Si PostgreSQL ou Container Apps y sont refusés malgré tout (offre restreinte dans la région), essayer une autre région de la liste. Non vérifié : que la stratégie laisse passer les ressources « globales » (zone DNS privée, budget) ; en cas de refus de la zone DNS, `enable_vnet = false` s'en passe.

### Fournisseurs de ressources

Un abonnement neuf n'a pas forcément `Microsoft.App`, `Microsoft.OperationalInsights` ni `Microsoft.DBforPostgreSQL`. Depuis azurerm 5.0, le fournisseur Terraform n'en enregistre plus aucun par défaut (`resource_provider_registrations = "none"`) ; ce code lui demande d'enregistrer la liste utile (`resource_providers_to_register`, dans `versions.tf`) : `Microsoft.App`, `Microsoft.OperationalInsights`, `Microsoft.Insights` (Application Insights, alertes), `Microsoft.DBforPostgreSQL`, `Microsoft.Network`, `Microsoft.Consumption`, `Microsoft.Cache`. Il faut être propriétaire ou contributeur de l'abonnement. À la main, si besoin (`register_resource_providers = false`) :

```bash
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.OperationalInsights
az provider register --namespace Microsoft.DBforPostgreSQL
az provider show --namespace Microsoft.App --query registrationState   # attendre « Registered »
```

### Budget

Le budget (`monthly_budget`, 20 $ par défaut, `budget_alert_email` obligatoire) couvre tout l'abonnement, groupe géré par Container Apps compris. Il prévient, il n'arrête rien, et les coûts arrivent avec jusqu'à 72 heures de retard. Avec la configuration par défaut (environ 113 $/mois si tout tourne en continu), l'alerte à 50 % arrive en quelques jours : c'est voulu. Si Azure refuse de créer le budget pour cette offre, passer `enable_budget = false` et suivre la consommation dans l'Education Hub. Le vrai garde-fou reste le crédit : épuisé, l'abonnement et ses ressources sont désactivés.

## Publier les images

Les images viennent de GitHub Container Registry : `ghcr.io/samsteeven/devpulse-<service>:<image_tag>` (identity, registry, monitoring-api, monitoring-worker, incident, alerting, ai-worker, gateway, web ; demo n'est pas déployé). Elles doivent être `linux/amd64`, seule architecture acceptée par Container Apps.

1. Onglet **Actions** du dépôt > workflow « Publier les images » > **Run workflow**. Le résumé donne l'étiquette (SHA court du commit) à passer dans `image_tag`.
2. Publiés depuis ce dépôt public, les paquets en héritent la visibilité : le 3/11/2026, les neuf images de `0602669` se lisaient sans identifiants (vérifié par une lecture anonyme du registre). Un dépôt privé donnerait des paquets privés ; il faudrait alors fournir des identifiants : `registry_username` (compte GitHub) et `registry_password`, un jeton d'accès personnel *classic* avec `read:packages`, de préférence par `TF_VAR_registry_password`. Terraform ajoute alors le bloc `registry` et son secret à chaque application.

## Déployer

Prérequis : Terraform 1.9 ou plus, Azure CLI.

```bash
az login
az account set --subscription "<id de l'abonnement>"
cd infrastructure/terraform-azure
cp terraform.tfvars.example terraform.tfvars   # location, image_tag, budget_alert_email
terraform init
terraform plan -out tfplan                      # à relire : premier plan jamais lancé
terraform apply tfplan
terraform output app_url
```

Durée de création non mesurée ; PostgreSQL et l'environnement Container Apps sont les plus lents. Pour changer de version : publier les images, changer `image_tag`, `terraform apply` (une nouvelle révision par application).

## Supervision

Rien de ce qui suit n'a tourné : ni l'export vers Application Insights, ni les requêtes, ni les règles. Les noms de tables et de colonnes viennent de la documentation Microsoft, la forme exacte des lignes écrites par l'exportateur est supposée.

### Ce qui part où

Avec `enable_application_insights` (vrai par défaut), les huit services .NET (identity, registry, monitoring-api, monitoring-worker, incident, alerting, ai-worker, gateway) reçoivent `APPLICATIONINSIGHTS_CONNECTION_STRING`, tirée du secret Container Apps `appinsights-connection`. `DevPulse.Observability` exporte alors traces, métriques et journaux avec l'exportateur Azure Monitor OpenTelemetry. web (nginx) et rabbitmq n'exportent rien : seule leur sortie standard arrive dans Log Analytics.

L'exportateur prend la ressource OpenTelemetry `service.name` comme nom de rôle (`cloud_RoleName`, `AppRoleName` dans l'espace) : **api-gateway, identity-service, registry-service, monitoring-api, monitoring-worker, incident-service, alerting-service, ai-worker**, posés dans le code (`AddDevPulseObservability`), et non les noms des Container Apps (`gateway`, `identity`…). L'instance (`AppRoleInstance`) est le `service.instance.id` généré au démarrage : un identifiant par processus, qui change à chaque révision (non vérifié).

| Pour | Aller dans |
|---|---|
| Suivre une requête de bout en bout (passerelle, API, RabbitMQ, worker) | Application Insights > **Transaction search**, puis une transaction ; le contexte de trace traverse RabbitMQ (en-tête `traceparent` des messages) |
| Voir qui appelle qui | Application Insights > **Application map** (un nœud par nom de rôle, PostgreSQL et RabbitMQ en dépendances) |
| Écrire du KQL | Application Insights > **Logs** (tables classiques `requests`, `dependencies`, `traces`, `customMetrics`) ou espace Log Analytics > **Logs** (`AppRequests`, `AppDependencies`, `AppTraces`, `AppMetrics`, et les journaux des conteneurs `ContainerAppConsoleLogs_CL`) |
| Les alertes de la plateforme | Monitor > **Alerts** ; un courriel à `budget_alert_email` |

Chaque ligne de journal part deux fois : en JSON sur la sortie standard (journaux des conteneurs) et dans `AppTraces`. Les deux comptent dans les plafonds. Levier non appliqué : `Serilog__MinimumLevel__Default = "Warning"` dans `common_env` réduirait les deux.

### Plafonds et volume

- **Deux plafonds, le plus petit l'emporte** (Microsoft Learn) : `app_insights_daily_cap_gb` (0,1 Go/jour) pour la télémétrie, `daily_quota_gb` (0,15 Go/jour) pour tout l'espace, Application Insights compris. Le total reste donc sous 4,65 Go par mois, dans les 5 Go gratuits, et les journaux des conteneurs gardent au moins 0,05 Go/jour. Un `precondition` refuse `app_insights_daily_cap_gb` au-dessus de `daily_quota_gb`. Sans valeur, azurerm poserait 100 Go/jour.
- **Échantillonnage.** Les métriques ne sont jamais échantillonnées : les règles n'en dépendent pas. D'après le README de l'exportateur, les traces le sont à la source, à 5 traces par seconde au plus (`RateLimitedSampler`, valeur par défaut) ; l'extension `AddAzureMonitorTraceExporter` que le code utilise installe bien un échantillonneur (`SetSampler`, code source de l'exportateur, branche principale), réglable par `OTEL_TRACES_SAMPLER` et `OTEL_TRACES_SAMPLER_ARG`. `app_insights_sampling_percentage` (100 par défaut) ajoute un échantillonnage à l'ingestion, que Microsoft déconseille (traces coupées) : levier de secours seulement, sans redéploiement des images.
- **Volume non mesuré.** Chaque service exporte ses séries de métriques une fois par minute, en delta (intervalle par défaut de l'exportateur Azure Monitor, 60 s). Vers Application Insights seul, le code ne garde que les mesures `DevPulse.*` et `http.server.request.duration` (vue OpenTelemetry dans `DevPulse.Observability`, testée) : le runtime .NET, le client HTTP et Kestrel ne partent pas, faute de quoi le plafond de 0,1 Go/jour aurait sans doute été atteint chaque jour. Il reste quelques dizaines de séries par service toujours démarré ; volume réel à mesurer le premier jour avec la troisième requête ci-dessous. Si le plafond est atteint malgré tout, plus rien n'arrive jusqu'à la remise à zéro de l'espace : « Plus aucune vérification » se déclenche (vérifier d'abord le plafond, bandeau dans le portail) et les quatre autres règles sont aveugles. Levier : relever les deux plafonds, au-delà des 5 Go gratuits à 2,76 $/Go (`docs/architecture/cout-azure.md`).
- `daily_data_cap_notifications_enabled` : Azure écrit aux administrateurs de l'abonnement quand le plafond d'Application Insights est atteint (comportement non vérifié sur une ressource basée sur un espace de travail).

### Les cinq règles

`enable_platform_alerts` (vrai par défaut, demande `enable_application_insights`) crée cinq règles de recherche dans les journaux (`azurerm_monitor_scheduled_query_rules_alert_v2`), mêmes titres que les règles Grafana locales. Elles interrogent la table `AppMetrics` de l'espace Log Analytics (leur portée), évaluées toutes les `platform_alert_frequency_minutes` (5 par défaut ; 10 ou 15 coûtent moins, voir le coût).

| Règle | Métrique (instrument OpenTelemetry) | Condition | Période lue | Gravité |
|---|---|---|---|---|
| Plus aucune vérification | `devpulse.checks` (compteur) | aucune hausse sur la dernière fenêtre (= fréquence) alors qu'il y en a eu dans l'heure | 1 h | 0 |
| Message en file morte | `devpulse.messaging.consumed`, `outcome` = `dead_lettered` | somme > 0 | 15 min | 0 |
| Outbox en retard | `devpulse.outbox.oldest.age` (jauge, s) | maximum > 60 | fréquence | 0 |
| Délai de notification au-dessus de 10 s | `devpulse.alerts.notification.latency` (histogramme, ms) | **moyenne** > 10 000 | 15 min | 2 |
| Alertes abandonnées | `devpulse.alerts.deliveries`, `outcome` = `failed` | somme > 0 | 15 min | 0 |

- **Courriel.** Groupe d'actions `<projet>-<environnement>-platform-alerts`, nom court « DevPulse », un destinataire : `budget_alert_email`, schéma d'alerte commun. Les règles sont à état (`auto_mitigation_enabled`) : un courriel au déclenchement, un autre à la résolution (après trois périodes sans la condition pour une fréquence de 5 à 15 min), rien entre les deux.
- **Moyenne, pas 95e centile.** L'exportateur réduit un histogramme à minimum, maximum, somme et nombre, sans les seaux (Microsoft Learn : le support des percentiles est « underway ») : la règle calcule `sum(Sum) / sum(ItemCount)`. Elle est moins sensible que la règle Grafana (p95) : quelques notifications très lentes peuvent rester sous une moyenne de 10 s. Variante plus stricte, non retenue : `max(Max) > 10000` (une seule notification lente suffit).
- **Autres écarts avec Grafana.** Pas de délai « for » (une seule période d'évaluation). « Plus aucune vérification » regarde la dernière fenêtre (5 min au moins, contre 3 min en local) et se résout d'elle-même au bout d'une heure sans vérification, quand l'heure de référence est vide à son tour. Les métriques arrivent dans `AppMetrics` avec quelques minutes de retard (non mesuré) : un retard plus long que la fenêtre ferait une fausse alerte.
- **Création.** `skip_query_validation = true` : la table `AppMetrics` n'existe sans doute pas avant les premières métriques, et la validation de la requête ferait échouer l'`apply` (supposé). Une erreur de KQL ne se verrait donc qu'à l'évaluation : relire les règles dans le portail (Monitor > Alerts > Alert rules > la règle > historique) après le premier déploiement.
- **Règle générée par Azure.** Avec Application Insights, Azure crée une règle de détection intelligente « Failure Anomalies » et son groupe d'actions, qui écrit aux propriétaires de l'abonnement. Le bloc `features` de `versions.tf` la désactive à la création (`disable_generated_rule`) ; elle reste dans le groupe de ressources, hors de l'état Terraform.

### Interroger les métriques

Dans l'espace Log Analytics > Logs. `Name` est le nom de l'instrument, points compris ; les étiquettes sont dans `Properties` ; un compteur vaut sa hausse depuis l'export précédent dans `Sum`, un histogramme a `Sum`, `ItemCount`, `Min` et `Max`.

```kusto
// Vérifications par minute et par état, sur 6 heures
AppMetrics
| where TimeGenerated > ago(6h) and Name == "devpulse.checks"
| summarize Checks = sum(Sum) by bin(TimeGenerated, 1m), Status = tostring(Properties["status"])
| render timechart
```

```kusto
// Délai de notification par événement : moyenne et pire cas, par quart d'heure
AppMetrics
| where TimeGenerated > ago(1d) and Name == "devpulse.alerts.notification.latency"
| summarize AverageMs = sum(Sum) / sum(ItemCount), WorstMs = max(Max) by bin(TimeGenerated, 15m), Event = tostring(Properties["event"])
```

```kusto
// Ce que coûtent les métriques : volume facturé par nom sur 24 heures, à lancer le premier jour
AppMetrics
| where TimeGenerated > ago(1d)
| summarize Rows = count(), MB = sum(_BilledSize) / 1e6 by Name, AppRoleName
| order by MB desc
```

Depuis Application Insights > Logs, mêmes données sous les noms classiques : `customMetrics | where name == "devpulse.checks" | summarize sum(valueSum) by tostring(customDimensions["status"])`. Volume par table de tout l'espace : `Usage | where TimeGenerated > ago(1d) | summarize Go = sum(Quantity) / 1000 by DataType`.

### Équivalent local

En local, la même télémétrie part en OTLP vers `infrastructure/observability` : l'image `grafana/otel-lgtm` de `docker-compose.yml` (Grafana sur http://localhost:3000, Prometheus, Tempo, Loki), le tableau de bord `dashboards/devpulse.json` et les règles `alerting/devpulse-rules.yaml`, **mêmes cinq titres** qu'ici. Les noms diffèrent par la conversion Prometheus (`devpulse_checks_total`, `devpulse_alerts_notification_latency_milliseconds_bucket`…), et le délai de notification y est un vrai 95e centile, calculé sur les seaux.

## Arrêter les frais

- **Mettre les réplicas à 0 ne suffit pas** : PostgreSQL, RabbitMQ et les consommateurs (min 1) tournent toujours, ainsi que le répartiteur et les IP publiques de l'environnement si `enable_vnet`.
- **Arrêter PostgreSQL** entre deux séances : `az postgres flexible-server stop --resource-group <resource_group_name> --name <postgres_server_name>` (valeurs dans `terraform output`). Le calcul n'est plus facturé, le stockage si. Azure le **redémarre seul au bout de 7 jours** ; `az postgres flexible-server start` pour reprendre plus tôt. Les services qui en dépendent échouent pendant l'arrêt.
- **`terraform destroy`** : le seul moyen de revenir à 0. Tout disparaît, données comprises (pas de `prevent_destroy`, pas d'instantané final). Le groupe géré par Container Apps est supprimé avec l'environnement (comportement attendu, non vérifié). La règle « Failure Anomalies » et son groupe d'actions, créés par Azure hors de l'état, partent avec le groupe de ressources : `versions.tf` lève pour cela la protection `prevent_deletion_if_contains_resources` (non vérifié).

Coût détaillé : [`docs/architecture/cout-azure.md`](../../docs/architecture/cout-azure.md).
