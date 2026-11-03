# --- RabbitMQ ---
# Azure n'a pas de RabbitMQ managé. Par défaut : le conteneur officiel dans l'environnement, entrée TCP interne.
# Son stockage est ÉPHÉMÈRE : un redémarrage (déploiement, maintenance, plantage) perd les messages déjà en file.
# L'outbox des services ne protège que les événements pas encore confirmés par le courtier. Acceptable pour une
# démonstration, pas pour une production.
# Sinon : un courtier externe (external_rabbitmq_*), et aucun conteneur.

locals {
  external_rabbitmq = var.external_rabbitmq_host != ""

  # Une application à entrée TCP se joint par son nom et son port exposé depuis le même environnement.
  rabbitmq_container = {
    host  = "rabbitmq"
    port  = 5672
    tls   = false
    user  = var.rabbitmq_user
    vhost = "/"
  }

  rabbitmq_external = {
    host  = var.external_rabbitmq_host
    port  = var.external_rabbitmq_port
    tls   = var.external_rabbitmq_use_tls
    user  = var.external_rabbitmq_user
    vhost = var.external_rabbitmq_vhost
  }

  rabbitmq          = local.external_rabbitmq ? local.rabbitmq_external : local.rabbitmq_container
  rabbitmq_password = local.external_rabbitmq ? var.external_rabbitmq_password : random_password.mq.result
}

resource "azurerm_container_app" "rabbitmq" {
  count = local.external_rabbitmq ? 0 : 1

  # Le nom sert d'adresse aux autres applications (rabbitmq:5672).
  name                         = "rabbitmq"
  container_app_environment_id = azurerm_container_app_environment.this.id
  resource_group_name          = azurerm_resource_group.this.name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"

  secret {
    name  = "rabbitmq-password"
    value = random_password.mq.result
  }

  # Entrée TCP interne : joignable par les autres applications de l'environnement seulement.
  ingress {
    external_enabled = false
    transport        = "tcp"
    target_port      = 5672
    exposed_port     = 5672

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    # Un seul nœud, toujours démarré : rien ne réveillerait un courtier arrêté.
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "rabbitmq"
      image  = var.rabbitmq_image
      cpu    = lookup(var.app_resources, "rabbitmq", local.default_resources).cpu
      memory = lookup(var.app_resources, "rabbitmq", local.default_resources).memory

      env {
        name  = "RABBITMQ_DEFAULT_USER"
        value = var.rabbitmq_user
      }

      env {
        name        = "RABBITMQ_DEFAULT_PASS"
        secret_name = "rabbitmq-password"
      }

      # Démarrage d'Erlang et de RabbitMQ : jusqu'à 2 minutes avant que la sonde de vie prenne le relais.
      startup_probe {
        transport               = "TCP"
        port                    = 5672
        interval_seconds        = 5
        failure_count_threshold = 24
      }

      liveness_probe {
        transport = "TCP"
        port      = 5672
      }
    }
  }

  tags = local.tags
}

# --- Redis (facultatif) ---
# Seulement pour le fond de panier SignalR d'alerting, utile à partir de deux réplicas. Pas d'Azure Cache for Redis
# Basic C0 : l'offre est retirée (30/09/2028) et la création de nouveaux caches bloquée depuis le 1er octobre 2026
# d'après l'annonce Microsoft. Remplaçant : Azure Managed Redis, TLS sur le port de la base (lu sur la ressource,
# 10000 d'habitude), authentification par clé d'accès.
# Accès public avec clé et TLS : un point de terminaison privé coûterait davantage que le cache.

resource "azurerm_managed_redis" "this" {
  count = var.enable_redis ? 1 : 0

  name                      = "${local.name}-redis-${random_string.suffix.result}"
  location                  = azurerm_resource_group.this.location
  resource_group_name       = azurerm_resource_group.this.name
  sku_name                  = var.redis_sku
  high_availability_enabled = var.redis_high_availability
  public_network_access     = "Enabled"

  default_database {
    access_keys_authentication_enabled = true
    client_protocol                    = "Encrypted"
    # Un seul point d'accès, sans protocole de cluster côté client.
    clustering_policy = "EnterpriseCluster"
  }

  tags = local.tags
}

locals {
  redis_connection_string = try(
    "${azurerm_managed_redis.this[0].hostname}:${azurerm_managed_redis.this[0].default_database[0].port},ssl=true,password=${azurerm_managed_redis.this[0].default_database[0].primary_access_key},abortConnect=false",
    "",
  )
}
