# Un service ECS Fargate : journaux, rôle d'exécution propre, définition de tâche, enregistrement Cloud Map
# facultatif, rattachement facultatif à un groupe cible de l'ALB.

terraform {
  required_providers {
    aws = {
      source = "hashicorp/aws"
    }
  }
}

resource "aws_cloudwatch_log_group" "this" {
  name              = "/ecs/${var.name_prefix}/${var.name}"
  retention_in_days = var.log_retention_days
}

# --- Rôle d'exécution : utilisé par l'agent ECS, pas par l'application ---
# Un rôle par service : chacun ne lit que ses propres secrets et ne tire que sa propre image.

data "aws_iam_policy_document" "assume_ecs_tasks" {
  statement {
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "execution" {
  name               = "${var.name_prefix}-${var.name}-exec"
  assume_role_policy = data.aws_iam_policy_document.assume_ecs_tasks.json
}

data "aws_iam_policy_document" "execution" {
  # GetAuthorizationToken ne se restreint pas à un dépôt.
  statement {
    sid       = "EcrAuth"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }

  statement {
    sid = "EcrPull"
    actions = [
      "ecr:BatchCheckLayerAvailability",
      "ecr:BatchGetImage",
      "ecr:GetDownloadUrlForLayer",
    ]
    resources = [var.ecr_repository_arn]
  }

  statement {
    sid       = "Logs"
    actions   = ["logs:CreateLogStream", "logs:PutLogEvents"]
    resources = ["${aws_cloudwatch_log_group.this.arn}:*"]
  }

  # Secrets chiffrés par la clé gérée « aws/secretsmanager » : pas de droit KMS à ajouter.
  dynamic "statement" {
    for_each = length(var.secret_arns) > 0 ? [1] : []

    content {
      sid       = "Secrets"
      actions   = ["secretsmanager:GetSecretValue"]
      resources = var.secret_arns
    }
  }
}

resource "aws_iam_role_policy" "execution" {
  name   = "execution"
  role   = aws_iam_role.execution.id
  policy = data.aws_iam_policy_document.execution.json
}

# --- Définition de tâche ---

locals {
  # Les secrets passent par `secrets` (valueFrom) : ECS les lit au démarrage, ils n'apparaissent ni dans la
  # définition de tâche ni dans la console. Le reste passe par `environment`.
  #
  # Pas de `readonlyRootFilesystem` : ASP.NET Core écrit dans /tmp, et Fargate ne permet pas de monter un tmpfs.
  # Un volume éphémère dédié serait possible, à essayer service par service.
  container_definition = {
    name      = var.name
    image     = var.image
    essential = true

    portMappings = var.container_port == null ? [] : [
      {
        containerPort = var.container_port
        protocol      = "tcp"
      },
    ]

    environment = [for key in sort(keys(var.environment)) : { name = key, value = var.environment[key] }]
    secrets     = [for key in sort(keys(var.secrets)) : { name = key, valueFrom = var.secrets[key] }]

    logConfiguration = {
      logDriver = "awslogs"
      options = {
        "awslogs-group"         = aws_cloudwatch_log_group.this.name
        "awslogs-region"        = var.region
        "awslogs-stream-prefix" = "ecs"
      }
    }
  }
}

resource "aws_ecs_task_definition" "this" {
  family                   = "${var.name_prefix}-${var.name}"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = tostring(var.cpu)
  memory                   = tostring(var.memory)
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = var.task_role_arn

  container_definitions = jsonencode([local.container_definition])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = var.cpu_architecture
  }
}

# --- Découverte : <nom>.devpulse.local ---

resource "aws_service_discovery_service" "this" {
  count = var.enable_service_discovery ? 1 : 0

  name = var.name

  dns_config {
    namespace_id   = var.service_discovery_namespace_id
    routing_policy = "MULTIVALUE"

    dns_records {
      type = "A"
      ttl  = 10
    }
  }
}

# --- Service ---

resource "aws_ecs_service" "this" {
  name            = var.name
  cluster         = var.cluster_arn
  task_definition = aws_ecs_task_definition.this.arn
  desired_count   = var.desired_count

  capacity_provider_strategy {
    capacity_provider = var.capacity_provider
    weight            = 1
  }

  network_configuration {
    subnets          = var.subnet_ids
    security_groups  = var.security_group_ids
    assign_public_ip = var.assign_public_ip
  }

  dynamic "load_balancer" {
    for_each = var.attach_load_balancer ? [1] : []

    content {
      target_group_arn = var.target_group_arn
      container_name   = var.name
      container_port   = var.container_port
    }
  }

  dynamic "service_registries" {
    for_each = var.enable_service_discovery ? [1] : []

    content {
      registry_arn = aws_service_discovery_service.this[0].arn
    }
  }

  # Une seule tâche par défaut : la nouvelle démarre avant l'arrêt de l'ancienne.
  deployment_minimum_healthy_percent = 100
  deployment_maximum_percent         = 200

  # Si la nouvelle version n'atteint jamais l'état stable, ECS revient à la précédente.
  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  # Seulement valable avec un répartiteur. Laisse le temps des migrations EF Core au premier démarrage.
  health_check_grace_period_seconds = var.attach_load_balancer ? 90 : null

  propagate_tags = "SERVICE"
}
