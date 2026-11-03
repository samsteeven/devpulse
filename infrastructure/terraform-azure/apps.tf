# --- Configuration des conteneurs ---
# Noms des variables relus dans docker-compose.yml (profil apps) et dans le code des services.

locals {
  # Mêmes valeurs que l'ancre x-app-env de docker-compose.yml, sauf OTEL_EXPORTER_OTLP_ENDPOINT (pas de collecteur
  # ici) et RabbitMQ, donné seulement aux services qui l'utilisent (voir rabbitmq_env).
  common_env = {
    ASPNETCORE_ENVIRONMENT = "Production"
    DOTNET_ENVIRONMENT     = "Production"
    Jwt__PublicKey         = local.jwt_public_key_b64
    # Protection SSRF : jamais de cible privée en dehors du poste de développement.
    TargetPolicy__AllowPrivateTargets = "false"
    # L'entrée de Container Apps est en HTTPS : le cookie de rafraîchissement peut rester Secure.
    Auth__SecureCookie = "true"
  }

  migrate_env = {
    Database__MigrateOnStartup = "true"
  }

  # Identity, Registry et la passerelle n'utilisent pas RabbitMQ (aucun AddDevPulseMessaging dans leur code) :
  # ils ne reçoivent ni l'adresse ni le mot de passe du courtier.
  rabbitmq_env = merge(
    {
      RabbitMq__Host   = local.rabbitmq.host
      RabbitMq__Port   = tostring(local.rabbitmq.port)
      RabbitMq__UseTls = tostring(local.rabbitmq.tls)
      RabbitMq__User   = local.rabbitmq.user
    },
    { for key, value in { RabbitMq__VirtualHost = local.rabbitmq.vhost } : key => value if value != "/" },
  )

  # Appel par nom court dans l'environnement : http://<application> sur le port 80, que le proxy Envoy de
  # l'environnement renvoie vers le port cible (8080). Le proxy choisit l'application d'après l'en-tête Host.
  # Le nom long <application>.internal.<domaine par défaut> marche aussi ; le nom court suffit et ne dépend pas
  # du domaine généré.
  gateway_destinations = {
    identity   = "identity"
    registry   = "registry"
    monitoring = "monitoring-api"
    incident   = "incident"
    alerting   = "alerting"
  }

  ai_settings = {
    Ai__Provider = var.ai_provider
    Ai__BaseUrl  = var.ai_base_url
    Ai__Model    = var.ai_model
  }

  # Variables vides retirées : Ai__BaseUrl et Ai__Model ne servent qu'avec le fournisseur Llm.
  ai_env = { for key, value in local.ai_settings : key => value if value != "" }

  # Réseau de confiance pour X-Forwarded-For : le sous-réseau de l'environnement. Sans réseau virtuel, la plage
  # des proxys n'est pas connue : rien n'est déclaré et la passerelle ignore l'en-tête.
  forwarded_env = { for key, value in { ForwardedHeaders__TrustedNetworks__0 = local.apps_subnet_cidr } : key => value if var.enable_vnet }

  environment = {
    # nginx relaie /api/ et /hubs/ vers la passerelle, avec l'en-tête Host de la passerelle (nginx.conf.template).
    "web" = {
      GATEWAY_URL = "http://gateway"
    }
    "gateway" = merge(
      local.common_env,
      { for cluster, app in local.gateway_destinations : "ReverseProxy__Clusters__${cluster}__Destinations__main__Address" => "http://${app}/" },
      local.forwarded_env,
      { RateLimiting__AuthPermitPerMinute = tostring(var.auth_permit_per_minute) },
    )
    "identity"       = merge(local.common_env, local.migrate_env)
    "registry"       = merge(local.common_env, local.migrate_env)
    "monitoring-api" = merge(local.common_env, local.migrate_env, local.rabbitmq_env)
    "monitoring-worker" = merge(
      local.common_env,
      local.rabbitmq_env,
      {
        Registry__BaseUrl          = "http://registry/"
        Database__MigrateOnStartup = "false"
      },
    )
    "incident"  = merge(local.common_env, local.migrate_env, local.rabbitmq_env)
    "alerting"  = merge(local.common_env, local.migrate_env, local.rabbitmq_env)
    "ai-worker" = merge(local.common_env, local.rabbitmq_env, local.ai_env)
  }

  # Variable d'environnement => nom du secret Container Apps (valeurs dans secrets.tf).
  secret_env = {
    "web"     = {}
    "gateway" = {}
    "identity" = {
      ConnectionStrings__Database = "db-identity"
      Jwt__PrivateKey             = "jwt-private-key"
    }
    "registry" = {
      ConnectionStrings__Database = "db-registry"
      InternalApi__Key            = "internal-api-key"
    }
    "monitoring-api" = {
      ConnectionStrings__Database = "db-monitoring"
      RabbitMq__Password          = "rabbitmq-password"
    }
    "monitoring-worker" = {
      ConnectionStrings__Database = "db-monitoring"
      InternalApi__Key            = "internal-api-key"
      RabbitMq__Password          = "rabbitmq-password"
    }
    "incident" = {
      ConnectionStrings__Database = "db-incident"
      RabbitMq__Password          = "rabbitmq-password"
    }
    # Sans Redis__ConnectionString, alerting n'a pas de fond de panier SignalR : correct avec un seul réplica.
    "alerting" = merge(
      {
        ConnectionStrings__Database = "db-alerting"
        RabbitMq__Password          = "rabbitmq-password"
      },
      { for key, secret in { Redis__ConnectionString = "redis-connection" } : key => secret if var.enable_redis },
    )
    "ai-worker" = merge(
      { RabbitMq__Password = "rabbitmq-password" },
      { for key, secret in { Ai__ApiKey = "ai-api-key" } : key => secret if local.ai_api_key_enabled },
    )
  }

  # Secrets déclarés dans chaque application : ceux que ses variables référencent, plus le mot de passe du
  # registre si les images sont privées. Liste de noms (non sensibles) : les valeurs sont lues dans le bloc.
  app_secret_names = {
    for app, envs in local.secret_env :
    app => toset(concat(values(envs), local.registry_auth ? ["registry-password"] : []))
  }

  registry_server = split("/", var.image_registry)[0]
}

# --- Applications ---

resource "azurerm_container_app" "app" {
  for_each = local.apps

  # Le nom est aussi l'adresse interne (http://gateway, http://registry…) : pas de préfixe.
  name                         = each.key
  container_app_environment_id = azurerm_container_app_environment.this.id
  resource_group_name          = azurerm_resource_group.this.name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"

  dynamic "secret" {
    for_each = local.app_secret_names[each.key]

    content {
      name  = secret.value
      value = local.secret_values[secret.value]
    }
  }

  # Paquets GHCR privés seulement ; publics, aucun identifiant n'est nécessaire.
  dynamic "registry" {
    for_each = local.registry_auth ? [local.registry_server] : []

    content {
      server               = registry.value
      username             = var.registry_username
      password_secret_name = "registry-password"
    }
  }

  dynamic "ingress" {
    for_each = each.value.ingress == null ? [] : [each.value.ingress]

    content {
      external_enabled = ingress.value == "external"
      target_port      = 8080
      transport        = "auto"
      # Entrée publique : HTTP redirigé vers HTTPS. Entrées internes : appelées en http://<application> depuis
      # l'environnement ; sans cette option, le proxy répondrait par une redirection vers HTTPS que nginx et YARP
      # relaieraient telle quelle. Le trafic ne quitte pas l'environnement.
      allow_insecure_connections = ingress.value == "internal"

      traffic_weight {
        latest_revision = true
        percentage      = 100
      }
    }
  }

  template {
    min_replicas = local.replicas[each.key].min
    max_replicas = local.replicas[each.key].max

    container {
      name   = each.key
      image  = "${var.image_registry}/devpulse-${each.key}:${var.image_tag}"
      cpu    = lookup(var.app_resources, each.key, local.default_resources).cpu
      memory = lookup(var.app_resources, each.key, local.default_resources).memory

      dynamic "env" {
        for_each = local.environment[each.key]

        content {
          name  = env.key
          value = env.value
        }
      }

      dynamic "env" {
        for_each = local.secret_env[each.key]

        content {
          name        = env.key
          secret_name = env.value
        }
      }

      # Les migrations tournent au démarrage (Database__MigrateOnStartup) : jusqu'à 2 min 30 avant que la sonde
      # de vie ne relance le conteneur.
      dynamic "startup_probe" {
        for_each = each.value.liveness == null ? [] : [each.value.liveness]

        content {
          transport               = "HTTP"
          port                    = 8080
          path                    = startup_probe.value
          interval_seconds        = 5
          failure_count_threshold = 30
        }
      }

      dynamic "liveness_probe" {
        for_each = each.value.liveness == null ? [] : [each.value.liveness]

        content {
          transport = "HTTP"
          port      = 8080
          path      = liveness_probe.value
        }
      }

      dynamic "readiness_probe" {
        for_each = each.value.readiness == null ? [] : [each.value.readiness]

        content {
          transport = "HTTP"
          port      = 8080
          path      = readiness_probe.value
        }
      }
    }
  }

  tags = local.tags

  lifecycle {
    # Sans entrée HTTP, rien ne réveille une application arrêtée.
    precondition {
      condition     = each.value.ingress != null || local.replicas[each.key].min >= 1
      error_message = "${each.key} n'a pas d'entrée HTTP : replicas.min doit valoir au moins 1."
    }

    # Sans Redis, les clients SignalR connectés à un réplica ne recevraient pas les notifications d'un autre.
    precondition {
      condition     = each.key != "alerting" || var.enable_redis || local.replicas[each.key].max == 1
      error_message = "alerting : plus d'un réplica demande enable_redis = true (fond de panier SignalR)."
    }
  }

  # Les bases doivent exister avant les migrations. web peut démarrer avant la passerelle : nginx échoue alors à
  # résoudre « gateway » et Container Apps le relance jusqu'à ce qu'elle existe.
  depends_on = [
    azurerm_postgresql_flexible_server_database.this,
    azurerm_postgresql_flexible_server_firewall_rule.azure_services,
  ]
}
