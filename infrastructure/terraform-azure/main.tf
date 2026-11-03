# DevPulse sur Azure Container Apps. Personne n'a encore lancé `terraform plan` sur ce code : voir README.md.

locals {
  name = "${var.project}-${var.environment}"

  tags = {
    project     = var.project
    environment = var.environment
    managed-by  = "terraform"
  }

  # Applications construites à partir du dépôt (images GHCR). rabbitmq est à part (messaging.tf), demo n'est pas
  # déployé. Toutes écoutent sur 8080.
  # - ingress : external (public, HTTPS), internal (joignable depuis l'environnement seulement) ou null (worker) ;
  # - liveness / readiness : chemins des sondes HTTP, null pour les workers.
  apps = {
    "web"               = { ingress = "external", liveness = "/", readiness = "/" }
    "gateway"           = { ingress = "internal", liveness = "/health/live", readiness = "/health/live" }
    "identity"          = { ingress = "internal", liveness = "/health/live", readiness = "/health/ready" }
    "registry"          = { ingress = "internal", liveness = "/health/live", readiness = "/health/ready" }
    "monitoring-api"    = { ingress = "internal", liveness = "/health/live", readiness = "/health/ready" }
    "incident"          = { ingress = "internal", liveness = "/health/live", readiness = "/health/ready" }
    "alerting"          = { ingress = "internal", liveness = "/health/live", readiness = "/health/ready" }
    "monitoring-worker" = { ingress = null, liveness = null, readiness = null }
    "ai-worker"         = { ingress = null, liveness = null, readiness = null }
  }

  # Réplicas par défaut, choisis pour le coût :
  # - 0 : l'application s'arrête après 5 minutes sans requête et redémarre à froid à la suivante ;
  # - 1 : incident et alerting consomment des files RabbitMQ (rien ne les réveille sinon) ; registry est appelé
  #   toutes les 30 s par monitoring-worker et ne s'arrêterait jamais : à min 1 il peut être facturé au tarif
  #   « inactif », à min 0 il serait toujours facturé au tarif actif (docs/architecture/cout-azure.md) ;
  # - max 1 partout : coût borné, et une seule instance d'alerting tant qu'il n'y a pas de fond de panier Redis.
  default_replicas = {
    "web"               = { min = 0, max = 1 }
    "gateway"           = { min = 0, max = 1 }
    "identity"          = { min = 0, max = 1 }
    "registry"          = { min = 1, max = 1 }
    "monitoring-api"    = { min = 0, max = 1 }
    "incident"          = { min = 1, max = 1 }
    "alerting"          = { min = 1, max = 1 }
    "monitoring-worker" = { min = 1, max = 1 }
    "ai-worker"         = { min = 1, max = 1 }
  }

  replicas = merge(local.default_replicas, var.replicas)

  default_resources = { cpu = 0.25, memory = "0.5Gi" }

  databases = {
    identity   = "devpulse_identity"
    registry   = "devpulse_registry"
    monitoring = "devpulse_monitoring"
    incident   = "devpulse_incident"
    alerting   = "devpulse_alerting"
  }
}

# Suffixe des noms globalement uniques (serveur PostgreSQL, Redis).
resource "random_string" "suffix" {
  length  = 6
  special = false
  upper   = false
}

resource "azurerm_resource_group" "this" {
  name     = "${local.name}-rg"
  location = var.location
  tags     = local.tags
}

# --- Journaux ---

# Le plafond quotidien protège le crédit : au-delà, Log Analytics cesse d'ingérer jusqu'au lendemain.
resource "azurerm_log_analytics_workspace" "this" {
  name                = "${local.name}-logs"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  sku                 = "PerGB2018"
  retention_in_days   = var.log_retention_days
  daily_quota_gb      = var.daily_quota_gb
  tags                = local.tags
}

# --- Environnement Container Apps ---

# Environnement « profils de charge de travail » (le type par défaut d'Azure) avec le seul profil Consumption :
# facturation à la seconde, pas de frais de gestion (ceux-ci ne s'appliquent qu'aux profils dédiés, points de
# terminaison privés et maintenance planifiée). Sous-réseau minimal /27 délégué à Microsoft.App/environments
# (un environnement « Consumption only » demanderait /23 non délégué).
# Environnement externe (pas `internal_load_balancer_enabled`) : web doit être public.
resource "azurerm_container_app_environment" "this" {
  name                       = "${local.name}-env"
  location                   = azurerm_resource_group.this.location
  resource_group_name        = azurerm_resource_group.this.name
  logs_destination           = "log-analytics"
  log_analytics_workspace_id = azurerm_log_analytics_workspace.this.id

  infrastructure_subnet_id = var.enable_vnet ? azurerm_subnet.apps[0].id : null
  # Groupe géré par la plateforme (répartiteur, IP publiques), nommé ici pour qu'il ne soit pas généré.
  infrastructure_resource_group_name = var.enable_vnet ? "${local.name}-aca-infra-rg" : null

  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }

  tags = local.tags
}
