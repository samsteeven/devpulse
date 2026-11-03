# Déploiement Azure : Container Apps avec Terraform

Variante Azure du déploiement AWS ([`infrastructure/terraform`](../terraform/README.md)), pensée pour un abonnement **Azure for Students** (environ 100 $ de crédit, sans carte bancaire) : tout ce qui peut s'arrêter s'arrête, les journaux sont plafonnés et un budget prévient par courriel.

**Ce code n'a jamais été appliqué.** Personne n'a lancé `terraform plan`, encore moins `apply`. Il a été écrit sur un poste sans Terraform : `fmt` et `validate` n'y ont pas été exécutés, c'est la CI qui les lance (`fmt -check`, `init -backend=false`, `validate`). Ces commandes vérifient la syntaxe, les types et les références, pas le comportement chez Azure.

## Ce qui est vérifié, et ce qui ne l'est pas

| Élément | Vérifié | Pas vérifié |
|---|---|---|
| Noms des variables d'environnement | Relus dans `docker-compose.yml` (profil `apps`) et dans le code (`RabbitMqOptions`, `ForwardedHeaders` de la passerelle, `Auth:SecureCookie`, `InternalApi`, `Redis:ConnectionString`, `Registry:BaseUrl`) | Que chaque service démarre avec exactement cette configuration |
| Clés JWT | Même dérivation PEM vers base64 DER que la version AWS (rejouée en .NET), même bloc `check` | Sur une clé produite par le fournisseur `tls` lui-même |
| Arguments des ressources | Relus le 3/11/2026 dans la documentation d'`hashicorp/azurerm` 5.x (branche principale) et dans son guide de migration 5.0 : `container_app`, `container_app_environment`, `postgresql_flexible_server` et `_database`, `subnet`, `private_dns_zone_virtual_network_link`, `log_analytics_workspace`, `consumption_budget_subscription`, `managed_redis`, bloc `provider` | `terraform validate` sur ce poste ; un vrai `plan` (valeurs refusées seulement par l'API Azure) |
| Comportement de Container Apps | Microsoft Learn : appel par `http://<application>`, redirection HTTP vers HTTPS si `allowInsecure` est faux, entrée TCP interne joignable par `<application>:<port exposé>` et possible **sans** réseau virtuel (seule l'entrée TCP externe en exige un), sous-réseau /27 délégué pour un environnement à profils de charge de travail, couples CPU/mémoire, WebSocket pris en charge | Rien n'a tourné : ni nginx vers `http://gateway`, ni YARP vers les API, ni le WebSocket `/hubs` à travers deux proxys |
| PostgreSQL 17 | Disponible sur Flexible Server (17.11, Microsoft Learn du 26/08/2026) ; `version = "17"` accepté par azurerm 5.x | Création réelle, disponibilité du B1ms dans la région autorisée de l'abonnement |
| Budget | Arguments relus | Qu'Azure l'accepte sur Azure for Students : la page « Understand Cost Management data » range encore cette offre (MS-AZR-0170P) parmi les non prises en charge, la page Education Hub conseille d'y créer des budgets |
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
- **Journaux.** Sortie standard des conteneurs vers Log Analytics (30 jours). Plafond `daily_quota_gb` = 0,15 Go par jour, soit au plus 4,65 Go par mois, sous les 5 Go gratuits ; au-delà, les journaux du jour sont perdus. Pas de collecteur OpenTelemetry : `OTEL_EXPORTER_OTLP_ENDPOINT` n'est pas posé.
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

Un abonnement neuf n'a pas forcément `Microsoft.App`, `Microsoft.OperationalInsights` ni `Microsoft.DBforPostgreSQL`. Depuis azurerm 5.0, le fournisseur Terraform n'en enregistre plus aucun par défaut (`resource_provider_registrations = "none"`) ; ce code lui demande d'enregistrer la liste utile (`resource_providers_to_register`, dans `versions.tf`) : `Microsoft.App`, `Microsoft.OperationalInsights`, `Microsoft.DBforPostgreSQL`, `Microsoft.Network`, `Microsoft.Consumption`, `Microsoft.Cache`. Il faut être propriétaire ou contributeur de l'abonnement. À la main, si besoin (`register_resource_providers = false`) :

```bash
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.OperationalInsights
az provider register --namespace Microsoft.DBforPostgreSQL
az provider show --namespace Microsoft.App --query registrationState   # attendre « Registered »
```

### Budget

Le budget (`monthly_budget`, 20 $ par défaut, `budget_alert_email` obligatoire) couvre tout l'abonnement, groupe géré par Container Apps compris. Il prévient, il n'arrête rien, et les coûts arrivent avec jusqu'à 72 heures de retard. Avec la configuration par défaut (environ 105 $/mois si tout tourne en continu), l'alerte à 50 % arrive en quelques jours : c'est voulu. Si Azure refuse de créer le budget pour cette offre, passer `enable_budget = false` et suivre la consommation dans l'Education Hub. Le vrai garde-fou reste le crédit : épuisé, l'abonnement et ses ressources sont désactivés.

## Publier les images

Les images viennent de GitHub Container Registry : `ghcr.io/samsteeven/devpulse-<service>:<image_tag>` (identity, registry, monitoring-api, monitoring-worker, incident, alerting, ai-worker, gateway, web ; demo n'est pas déployé). Elles doivent être `linux/amd64`, seule architecture acceptée par Container Apps.

1. Onglet **Actions** du dépôt > workflow « Publier les images » > **Run workflow**. Le résumé donne l'étiquette (SHA court du commit) à passer dans `image_tag`.
2. Les paquets GHCR sont **privés** à leur création. Au choix :
   - les rendre publics : page de chaque paquet > **Package settings** > **Change visibility** (neuf paquets) ;
   - ou fournir des identifiants : `registry_username` (compte GitHub) et `registry_password`, un jeton d'accès personnel *classic* avec `read:packages`, de préférence par `TF_VAR_registry_password`. Terraform ajoute alors le bloc `registry` et son secret à chaque application.

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

## Arrêter les frais

- **Mettre les réplicas à 0 ne suffit pas** : PostgreSQL, RabbitMQ et les consommateurs (min 1) tournent toujours, ainsi que le répartiteur et les IP publiques de l'environnement si `enable_vnet`.
- **Arrêter PostgreSQL** entre deux séances : `az postgres flexible-server stop --resource-group <resource_group_name> --name <postgres_server_name>` (valeurs dans `terraform output`). Le calcul n'est plus facturé, le stockage si. Azure le **redémarre seul au bout de 7 jours** ; `az postgres flexible-server start` pour reprendre plus tôt. Les services qui en dépendent échouent pendant l'arrêt.
- **`terraform destroy`** : le seul moyen de revenir à 0. Tout disparaît, données comprises (pas de `prevent_destroy`, pas d'instantané final). Le groupe géré par Container Apps est supprimé avec l'environnement (comportement attendu, non vérifié).

Coût détaillé : [`docs/architecture/cout-azure.md`](../../docs/architecture/cout-azure.md).
