resource "aws_ecs_cluster" "this" {
  name = local.name

  setting {
    name  = "containerInsights"
    value = var.container_insights ? "enabled" : "disabled"
  }
}

resource "aws_ecs_cluster_capacity_providers" "this" {
  cluster_name       = aws_ecs_cluster.this.name
  capacity_providers = ["FARGATE", "FARGATE_SPOT"]

  default_capacity_provider_strategy {
    capacity_provider = "FARGATE"
    weight            = 1
  }
}

# Zone DNS privée : la passerelle joint identity.devpulse.local:8080, etc. Enregistrements A vers l'IP privée de
# chaque tâche (mode awsvpc).
resource "aws_service_discovery_private_dns_namespace" "this" {
  name        = local.namespace
  description = "Decouverte des services DevPulse"
  vpc         = module.network.vpc_id
}

# --- Rôle des tâches : utilisé par l'application ---
# Aucune permission : les services n'appellent aucune API AWS. Le rôle existe pour qu'une tâche n'hérite jamais
# d'identifiants par un autre biais.

data "aws_caller_identity" "current" {}

data "aws_iam_policy_document" "assume_task_role" {
  statement {
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }

    condition {
      test     = "StringEquals"
      variable = "aws:SourceAccount"
      values   = [data.aws_caller_identity.current.account_id]
    }

    condition {
      test     = "ArnLike"
      variable = "aws:SourceArn"
      values   = ["arn:aws:ecs:${var.aws_region}:${data.aws_caller_identity.current.account_id}:*"]
    }
  }
}

resource "aws_iam_role" "task" {
  name               = "${local.name}-task"
  assume_role_policy = data.aws_iam_policy_document.assume_task_role.json
}

# --- Configuration des conteneurs ---

locals {
  secret_arn = {
    database = aws_secretsmanager_secret.database.arn
    jwt      = aws_secretsmanager_secret.jwt_private_key.arn
    internal = aws_secretsmanager_secret.internal_api_key.arn
    rabbitmq = aws_secretsmanager_secret.rabbitmq.arn
    redis    = aws_secretsmanager_secret.redis.arn
  }

  # Référence à une clé d'un secret JSON : ARN:clé:étape:version (étape et version vides = version courante).
  db_secret = { for key, db in local.databases : key => "${local.secret_arn.database}:${key}::" }

  jwt_env = {
    Jwt__PublicKey = local.jwt_public_key_b64
  }

  migrate_env = {
    Database__MigrateOnStartup = "true"
  }

  # Protection SSRF : jamais de cible privée en dehors du poste de développement.
  ssrf_env = {
    TargetPolicy__AllowPrivateTargets = "false"
  }

  rabbitmq_env = {
    RabbitMq__Host   = module.data.mq_host
    RabbitMq__Port   = "5671"
    RabbitMq__UseTls = "true"
    RabbitMq__User   = var.mq_username
  }

  rabbitmq_secrets = {
    RabbitMq__Password = local.secret_arn.rabbitmq
  }

  # Cluster YARP => service Cloud Map.
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

  environment = {
    "gateway" = merge(
      local.jwt_env,
      { for cluster, service in local.gateway_destinations : "ReverseProxy__Clusters__${cluster}__Destinations__main__Address" => "http://${service}.${local.namespace}:8080/" },
      { ForwardedHeaders__TrustedNetworks__0 = var.vpc_cidr },
    )
    "identity" = merge(
      local.jwt_env,
      local.migrate_env,
      { Auth__SecureCookie = tostring(local.https_enabled) },
    )
    "registry" = merge(
      local.jwt_env,
      local.migrate_env,
      local.ssrf_env,
    )
    "monitoring-api" = merge(
      local.jwt_env,
      local.migrate_env,
      local.ssrf_env,
      local.rabbitmq_env,
    )
    "monitoring-worker" = merge(
      local.ssrf_env,
      local.rabbitmq_env,
      { Registry__BaseUrl = "http://registry.${local.namespace}:8080/" },
    )
    "incident" = merge(
      local.jwt_env,
      local.migrate_env,
      local.rabbitmq_env,
    )
    "alerting" = merge(
      local.jwt_env,
      local.migrate_env,
      local.ssrf_env,
      local.rabbitmq_env,
    )
    "ai-worker" = merge(
      local.rabbitmq_env,
      local.ai_env,
    )
    "web" = {}
  }

  secrets = {
    "gateway" = {}
    "identity" = {
      ConnectionStrings__Database = local.db_secret.identity
      Jwt__PrivateKey             = local.secret_arn.jwt
    }
    "registry" = {
      ConnectionStrings__Database = local.db_secret.registry
      InternalApi__Key            = local.secret_arn.internal
    }
    "monitoring-api" = merge(local.rabbitmq_secrets, {
      ConnectionStrings__Database = local.db_secret.monitoring
    })
    "monitoring-worker" = merge(local.rabbitmq_secrets, {
      ConnectionStrings__Database = local.db_secret.monitoring
      InternalApi__Key            = local.secret_arn.internal
    })
    "incident" = merge(local.rabbitmq_secrets, {
      ConnectionStrings__Database = local.db_secret.incident
    })
    "alerting" = merge(local.rabbitmq_secrets, {
      ConnectionStrings__Database = local.db_secret.alerting
      Redis__ConnectionString     = "${local.secret_arn.redis}:connection_string::"
    })
    "ai-worker" = merge(
      local.rabbitmq_secrets,
      { for key, arn in { Ai__ApiKey = one(aws_secretsmanager_secret.ai_api_key[*].arn) } : key => arn if local.ai_api_key_enabled },
    )
    "web" = {}
  }

  # Secrets lisibles par le rôle d'exécution de chaque service. Listes écrites à la main (et non déduites de
  # `secrets`) pour que leur longueur soit connue au plan.
  secret_arns = {
    "gateway"           = []
    "identity"          = [local.secret_arn.database, local.secret_arn.jwt]
    "registry"          = [local.secret_arn.database, local.secret_arn.internal]
    "monitoring-api"    = [local.secret_arn.database, local.secret_arn.rabbitmq]
    "monitoring-worker" = [local.secret_arn.database, local.secret_arn.internal, local.secret_arn.rabbitmq]
    "incident"          = [local.secret_arn.database, local.secret_arn.rabbitmq]
    "alerting"          = [local.secret_arn.database, local.secret_arn.rabbitmq, local.secret_arn.redis]
    "ai-worker"         = concat([local.secret_arn.rabbitmq], aws_secretsmanager_secret.ai_api_key[*].arn)
    "web"               = []
  }
}

# --- Services ---

module "service" {
  source   = "./modules/ecs-service"
  for_each = local.services

  name        = each.key
  name_prefix = local.name
  region      = var.aws_region
  cluster_arn = aws_ecs_cluster.this.arn

  image              = "${aws_ecr_repository.this[each.key].repository_url}:${var.image_tag}"
  ecr_repository_arn = aws_ecr_repository.this[each.key].arn
  cpu                = lookup(var.service_resources, each.key, local.default_resources).cpu
  memory             = lookup(var.service_resources, each.key, local.default_resources).memory
  cpu_architecture   = var.cpu_architecture
  desired_count      = lookup(var.desired_counts, each.key, 1)
  capacity_provider  = each.value.worker && var.use_spot_for_workers ? "FARGATE_SPOT" : "FARGATE"

  container_port = each.value.port
  environment    = local.environment[each.key]
  secrets        = local.secrets[each.key]
  secret_arns    = local.secret_arns[each.key]
  task_role_arn  = aws_iam_role.task.arn

  subnet_ids         = module.network.task_subnet_ids
  security_group_ids = [local.task_security_group_ids[each.value.security_group]]
  assign_public_ip   = module.network.assign_public_ip

  attach_load_balancer = each.value.load_balancer
  target_group_arn     = each.value.load_balancer ? local.target_group_arns[each.key] : null

  enable_service_discovery       = each.value.discovery
  service_discovery_namespace_id = aws_service_discovery_private_dns_namespace.this.id

  log_retention_days = var.log_retention_days

  # Un service rattaché à un groupe cible exige que celui-ci soit déjà lié à un écouteur ; une stratégie de
  # fournisseur de capacité exige que le fournisseur soit associé au cluster.
  depends_on = [
    aws_ecs_cluster_capacity_providers.this,
    aws_lb_listener.http,
    aws_lb_listener.https,
    aws_lb_listener_rule.gateway,
  ]
}

locals {
  task_security_group_ids = {
    gateway  = aws_security_group.gateway.id
    web      = aws_security_group.web.id
    internal = aws_security_group.internal.id
    workers  = aws_security_group.workers.id
  }
}
