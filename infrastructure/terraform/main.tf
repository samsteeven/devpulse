# DevPulse sur ECS Fargate (ADR-008). Personne n'a encore lancé `terraform plan` sur ce code : voir README.md.

locals {
  name      = "${var.project}-${var.environment}"
  namespace = "devpulse.local"

  https_enabled = var.certificate_arn != ""

  # Rôle réseau de chaque conteneur :
  # - gateway et web reçoivent le trafic de l'ALB ;
  # - les API ne sont joignables que par la passerelle (et Registry par monitoring-worker), via Cloud Map ;
  # - les workers n'ont aucun port ouvert.
  services = {
    "gateway"           = { security_group = "gateway", port = 8080, load_balancer = true, discovery = true, worker = false }
    "web"               = { security_group = "web", port = 8080, load_balancer = true, discovery = false, worker = false }
    "identity"          = { security_group = "internal", port = 8080, load_balancer = false, discovery = true, worker = false }
    "registry"          = { security_group = "internal", port = 8080, load_balancer = false, discovery = true, worker = false }
    "monitoring-api"    = { security_group = "internal", port = 8080, load_balancer = false, discovery = true, worker = false }
    "incident"          = { security_group = "internal", port = 8080, load_balancer = false, discovery = true, worker = false }
    "alerting"          = { security_group = "internal", port = 8080, load_balancer = false, discovery = true, worker = false }
    "monitoring-worker" = { security_group = "workers", port = null, load_balancer = false, discovery = false, worker = true }
    "ai-worker"         = { security_group = "workers", port = null, load_balancer = false, discovery = false, worker = true }
  }

  default_resources = { cpu = 256, memory = 512 }

  databases = {
    identity   = "devpulse_identity"
    registry   = "devpulse_registry"
    monitoring = "devpulse_monitoring"
    incident   = "devpulse_incident"
    alerting   = "devpulse_alerting"
  }
}

module "network" {
  source = "./modules/network"

  name               = local.name
  cidr               = var.vpc_cidr
  enable_nat_gateway = var.enable_nat_gateway
}

module "data" {
  source = "./modules/data"

  name               = local.name
  vpc_id             = module.network.vpc_id
  private_subnet_ids = module.network.private_subnet_ids

  # La passerelle et le front n'ont accès à aucune donnée. Seul Alerting utilise Valkey.
  db_client_security_group_ids    = { internal = aws_security_group.internal.id, workers = aws_security_group.workers.id }
  mq_client_security_group_ids    = { internal = aws_security_group.internal.id, workers = aws_security_group.workers.id }
  cache_client_security_group_ids = { internal = aws_security_group.internal.id }

  db_instance_class        = var.db_instance_class
  db_engine_version        = var.db_engine_version
  db_allocated_storage     = var.db_allocated_storage
  db_username              = var.db_username
  db_password              = random_password.db.result
  db_backup_retention_days = var.db_backup_retention_days
  db_deletion_protection   = var.db_deletion_protection
  db_skip_final_snapshot   = var.db_skip_final_snapshot

  mq_instance_type  = var.mq_instance_type
  mq_engine_version = var.mq_engine_version
  mq_username       = var.mq_username
  mq_password       = random_password.mq.result

  cache_node_type      = var.cache_node_type
  cache_engine_version = var.cache_engine_version
  cache_auth_token     = random_password.cache.result
}

# --- ECR : un dépôt par service ---

resource "aws_ecr_repository" "this" {
  for_each = local.services

  name = "${var.project}/${each.key}"

  # Un tag ne peut pas être réécrit : `image_tag` désigne toujours la même image (pousser un SHA de commit).
  image_tag_mutability = "IMMUTABLE"
  force_delete         = var.ecr_force_delete

  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "this" {
  for_each = aws_ecr_repository.this

  repository = each.value.name

  policy = jsonencode({
    rules = [
      {
        rulePriority = 1
        description  = "Garder les 10 dernieres images"
        selection = {
          tagStatus   = "any"
          countType   = "imageCountMoreThan"
          countNumber = 10
        }
        action = {
          type = "expire"
        }
      },
    ]
  })
}
