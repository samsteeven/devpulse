variable "project" {
  description = "Préfixe des noms de ressources."
  type        = string
  default     = "devpulse"
}

variable "environment" {
  description = "Nom de l'environnement, ajouté aux noms de ressources."
  type        = string
  default     = "demo"

  validation {
    condition     = can(regex("^[a-z0-9-]{2,12}$", var.environment))
    error_message = "environment : 2 à 12 caractères parmi a-z, 0-9 et « - »."
  }
}

variable "location" {
  description = "Région Azure, par exemple francecentral. Pas de valeur par défaut : un abonnement Azure for Students n'accepte que quelques régions, propres à chaque abonnement (voir README)."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9]+$", var.location))
    error_message = "location : nom court de la région (francecentral, westeurope…), pas le nom affiché."
  }
}

variable "subscription_id" {
  description = "Abonnement Azure. Vide : ARM_SUBSCRIPTION_ID, sinon l'abonnement par défaut d'Azure CLI."
  type        = string
  default     = null
}

variable "register_resource_providers" {
  description = "Enregistre les fournisseurs de ressources utiles (Microsoft.App, Microsoft.DBforPostgreSQL…). Faux si le compte n'a pas le droit de le faire et qu'ils sont déjà enregistrés."
  type        = bool
  default     = true
}

# --- Réseau ---

variable "enable_vnet" {
  description = "Vrai : réseau virtuel, PostgreSQL sans accès public (sous-réseau délégué). Faux : pas de réseau virtuel, PostgreSQL public limité aux adresses Azure ; environ 25 $/mois de moins (répartiteur et IP publiques de l'environnement), voir docs/architecture/cout-azure.md."
  type        = bool
  default     = true
}

variable "vnet_cidr" {
  description = "Plage du réseau virtuel. Ne doit pas chevaucher les plages réservées par Container Apps (169.254.0.0/16, 172.30.0.0/16, 172.31.0.0/16, 192.0.2.0/24, 100.100.0.0/16)."
  type        = string
  default     = "10.60.0.0/16"
}

# --- Images ---

variable "image_registry" {
  description = "Registre et propriétaire des images : <registre>/<propriétaire>. Image d'un service : <image_registry>/devpulse-<service>:<image_tag>."
  type        = string
  default     = "ghcr.io/samsteeven"
}

variable "image_tag" {
  description = "Étiquette des images, la même pour tous les services (SHA court publié par le workflow « Publier les images »)."
  type        = string

  validation {
    condition     = length(var.image_tag) > 0
    error_message = "image_tag est obligatoire."
  }
}

variable "registry_username" {
  description = "Utilisateur du registre, seulement si les paquets sont privés (compte GitHub pour GHCR)."
  type        = string
  default     = ""
}

variable "registry_password" {
  description = "Mot de passe du registre (pour GHCR : jeton d'accès personnel avec read:packages). Vide : images publiques, aucun identifiant."
  type        = string
  default     = ""
  sensitive   = true
}

# --- Container Apps ---

variable "replicas" {
  description = "Réplicas minimum et maximum par application, fusionnés avec les valeurs par défaut de main.tf. min = 0 : arrêt après 5 minutes sans requête, démarrage à froid à la suivante."
  type = map(object({
    min = number
    max = number
  }))
  default = {}

  validation {
    condition     = alltrue([for app, r in var.replicas : r.min >= 0 && r.max >= 1 && r.min <= r.max])
    error_message = "replicas : 0 <= min <= max et max >= 1."
  }
}

variable "app_resources" {
  description = "CPU (vCPU) et mémoire par application, rabbitmq compris. Une application absente prend 0.25 / 0.5Gi. Seules les paires du plan Consommation sont valides (mémoire = 2 x CPU)."
  type = map(object({
    cpu    = number
    memory = string
  }))
  default = {}

  validation {
    condition = alltrue([
      for app, r in var.app_resources :
      contains([0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2], r.cpu) && r.memory == "${r.cpu * 2}Gi"
    ])
    error_message = "app_resources : cpu parmi 0.25, 0.5 … 2 et memory = 2 x cpu suivi de « Gi » (0.25 / \"0.5Gi\", 0.5 / \"1Gi\", 1 / \"2Gi\"…)."
  }
}

variable "auth_permit_per_minute" {
  description = "Connexions autorisées par minute et par adresse sur la passerelle (RateLimiting__AuthPermitPerMinute). Derrière le nginx de web, l'adresse vue est probablement la même pour tous les visiteurs : voir README."
  type        = number
  default     = 20
}

# --- Journaux ---

variable "daily_quota_gb" {
  description = "Plafond quotidien d'ingestion de Log Analytics, en Go. 0.15 Go/jour reste sous les 5 Go gratuits du mois ; au-delà du plafond, les journaux du jour sont perdus."
  type        = number
  default     = 0.15

  validation {
    condition     = var.daily_quota_gb > 0
    error_message = "daily_quota_gb doit être positif (pas d'ingestion illimitée dans cette configuration)."
  }
}

variable "log_retention_days" {
  description = "Rétention de Log Analytics, en jours (30 à 730 ; 31 jours inclus dans le prix d'ingestion)."
  type        = number
  default     = 30

  validation {
    condition     = var.log_retention_days >= 30 && var.log_retention_days <= 730
    error_message = "log_retention_days : entre 30 et 730."
  }
}

# --- PostgreSQL ---

variable "db_version" {
  description = "Version majeure de PostgreSQL (17, comme docker-compose.yml)."
  type        = string
  default     = "17"
}

variable "db_sku" {
  description = "SKU du serveur flexible. B_Standard_B1ms : celui des 750 heures gratuites pendant 12 mois (nouveaux clients, voir docs/architecture/cout-azure.md)."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "db_storage_mb" {
  description = "Stockage, en Mo. 32768 (32 Go) : la plus petite taille, et celle de l'offre gratuite."
  type        = number
  default     = 32768
}

variable "db_backup_retention_days" {
  description = "Rétention des sauvegardes automatiques, en jours (7 à 35)."
  type        = number
  default     = 7

  validation {
    condition     = var.db_backup_retention_days >= 7 && var.db_backup_retention_days <= 35
    error_message = "db_backup_retention_days : entre 7 et 35."
  }
}

variable "db_admin_login" {
  description = "Administrateur PostgreSQL. Les services l'utilisent tous (simplification, voir README)."
  type        = string
  default     = "devpulse"
}

# --- RabbitMQ ---

variable "rabbitmq_image" {
  description = "Image du conteneur RabbitMQ (même étiquette que docker-compose.yml). Ignorée si external_rabbitmq_host est renseigné."
  type        = string
  default     = "rabbitmq:4.3.7-management-alpine"
}

variable "rabbitmq_user" {
  description = "Utilisateur du conteneur RabbitMQ."
  type        = string
  default     = "devpulse"
}

variable "external_rabbitmq_host" {
  description = "Courtier externe (CloudAMQP…). Renseigné : aucun conteneur RabbitMQ n'est créé et tous les services pointent vers ce courtier."
  type        = string
  default     = ""
}

variable "external_rabbitmq_port" {
  description = "Port du courtier externe (5671 en AMQPS)."
  type        = number
  default     = 5671
}

variable "external_rabbitmq_use_tls" {
  description = "AMQPS vers le courtier externe."
  type        = bool
  default     = true
}

variable "external_rabbitmq_user" {
  description = "Utilisateur du courtier externe."
  type        = string
  default     = ""
}

variable "external_rabbitmq_password" {
  description = "Mot de passe du courtier externe."
  type        = string
  default     = ""
  sensitive   = true
}

variable "external_rabbitmq_vhost" {
  description = "Hôte virtuel du courtier externe. Chez CloudAMQP (offres partagées), c'est le nom d'utilisateur."
  type        = string
  default     = "/"
}

# --- Redis ---

variable "enable_redis" {
  description = "Crée un Azure Managed Redis pour le fond de panier SignalR d'alerting. Inutile tant qu'alerting n'a qu'un réplica."
  type        = bool
  default     = false
}

variable "redis_sku" {
  description = "SKU d'Azure Managed Redis (Balanced_B0 : le plus petit)."
  type        = string
  default     = "Balanced_B0"
}

variable "redis_high_availability" {
  description = "Haute disponibilité d'Azure Managed Redis (deux nœuds, coût environ doublé d'après la tarification à l'instance)."
  type        = bool
  default     = false
}

# --- IA ---

variable "ai_provider" {
  description = "Fournisseur d'analyse : Rules (sans appel externe) ou Llm (API compatible OpenAI)."
  type        = string
  default     = "Rules"

  validation {
    condition     = contains(["Rules", "Llm"], var.ai_provider)
    error_message = "ai_provider : Rules ou Llm."
  }
}

variable "ai_base_url" {
  description = "Base de l'API du modèle, terminée par « / ». Utilisée seulement avec ai_provider = Llm."
  type        = string
  default     = ""
}

variable "ai_model" {
  description = "Nom du modèle. Utilisé seulement avec ai_provider = Llm."
  type        = string
  default     = ""
}

variable "ai_api_key" {
  description = "Clé de l'API du modèle (facultative). Vide : aucun secret créé."
  type        = string
  default     = ""
  sensitive   = true
}

# --- Budget ---

variable "enable_budget" {
  description = "Crée le budget mensuel de l'abonnement. Faux si Azure le refuse (offre non prise en charge par Cost Management, voir README)."
  type        = bool
  default     = true
}

variable "monthly_budget" {
  description = "Budget mensuel de l'abonnement, dans sa devise (USD pour Azure for Students)."
  type        = number
  default     = 20

  validation {
    condition     = var.monthly_budget > 0
    error_message = "monthly_budget doit être positif."
  }
}

variable "budget_alert_email" {
  description = "Adresse qui reçoit les alertes du budget (50 %, 80 %, 100 % constatés, 100 % prévus)."
  type        = string

  validation {
    condition     = can(regex("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$", var.budget_alert_email))
    error_message = "budget_alert_email doit être une adresse électronique."
  }
}
