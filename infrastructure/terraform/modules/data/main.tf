# Données managées : une instance PostgreSQL, un broker RabbitMQ, un nœud Valkey. Tous en sous-réseaux privés,
# joignables seulement depuis les groupes de sécurité des tâches passés en entrée.

terraform {
  required_providers {
    aws = {
      source = "hashicorp/aws"
    }
  }
}

# --- PostgreSQL ---

resource "aws_security_group" "db" {
  name        = "${var.name}-db"
  description = "PostgreSQL : entree depuis les taches ECS uniquement"
  vpc_id      = var.vpc_id
}

resource "aws_vpc_security_group_ingress_rule" "db" {
  for_each = var.db_client_security_group_ids

  security_group_id            = aws_security_group.db.id
  referenced_security_group_id = each.value
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  description                  = "PostgreSQL depuis ${each.key}"
}

resource "aws_db_subnet_group" "this" {
  name       = "${var.name}-db"
  subnet_ids = var.private_subnet_ids
}

# Une seule instance et un seul utilisateur maître pour les cinq bases : chaque service crée la sienne à sa première
# migration (EF Core Migrate). Étape suivante : un utilisateur par service, limité à sa base.
resource "aws_db_instance" "this" {
  identifier     = "${var.name}-postgres"
  engine         = "postgres"
  engine_version = var.db_engine_version
  instance_class = var.db_instance_class

  allocated_storage = var.db_allocated_storage
  storage_type      = "gp3"
  storage_encrypted = true

  username = var.db_username
  password = var.db_password

  db_subnet_group_name   = aws_db_subnet_group.this.name
  vpc_security_group_ids = [aws_security_group.db.id]
  publicly_accessible    = false
  multi_az               = false

  backup_retention_period   = var.db_backup_retention_days
  deletion_protection       = var.db_deletion_protection
  skip_final_snapshot       = var.db_skip_final_snapshot
  final_snapshot_identifier = var.db_skip_final_snapshot ? null : "${var.name}-postgres-final"
  copy_tags_to_snapshot     = true

  auto_minor_version_upgrade = true
  apply_immediately          = true
}

# --- Amazon MQ (RabbitMQ) ---

resource "aws_security_group" "mq" {
  name        = "${var.name}-mq"
  description = "Amazon MQ : AMQPS depuis les taches ECS uniquement"
  vpc_id      = var.vpc_id
}

resource "aws_vpc_security_group_ingress_rule" "mq" {
  for_each = var.mq_client_security_group_ids

  security_group_id            = aws_security_group.mq.id
  referenced_security_group_id = each.value
  ip_protocol                  = "tcp"
  from_port                    = 5671
  to_port                      = 5671
  description                  = "AMQPS depuis ${each.key}"
}

# Instance unique : un redémarrage du broker (maintenance) interrompt les échanges. L'outbox des services garde
# les messages en base pendant ce temps.
resource "aws_mq_broker" "this" {
  broker_name        = "${var.name}-rabbitmq"
  engine_type        = "RabbitMQ"
  engine_version     = var.mq_engine_version
  host_instance_type = var.mq_instance_type
  deployment_mode    = "SINGLE_INSTANCE"

  publicly_accessible = false
  subnet_ids          = [var.private_subnet_ids[0]]
  security_groups     = [aws_security_group.mq.id]

  # Obligatoire pour RabbitMQ 3.13 et plus : Amazon MQ applique lui-même les versions mineures.
  auto_minor_version_upgrade = true
  apply_immediately          = true

  user {
    username = var.mq_username
    password = var.mq_password
  }
}

# --- ElastiCache (Valkey) ---

resource "aws_security_group" "cache" {
  name        = "${var.name}-cache"
  description = "Valkey : entree depuis les taches ECS uniquement"
  vpc_id      = var.vpc_id
}

resource "aws_vpc_security_group_ingress_rule" "cache" {
  for_each = var.cache_client_security_group_ids

  security_group_id            = aws_security_group.cache.id
  referenced_security_group_id = each.value
  ip_protocol                  = "tcp"
  from_port                    = 6379
  to_port                      = 6379
  description                  = "Valkey depuis ${each.key}"
}

resource "aws_elasticache_subnet_group" "this" {
  name       = "${var.name}-cache"
  subnet_ids = var.private_subnet_ids
}

# Sert uniquement de relais SignalR entre instances d'Alerting : perdre son contenu ne perd aucune donnée.
resource "aws_elasticache_replication_group" "this" {
  replication_group_id = "${var.name}-valkey"
  description          = "DevPulse : relais SignalR"
  engine               = "valkey"
  engine_version       = var.cache_engine_version
  node_type            = var.cache_node_type
  num_cache_clusters   = 1
  port                 = 6379

  subnet_group_name  = aws_elasticache_subnet_group.this.name
  security_group_ids = [aws_security_group.cache.id]

  at_rest_encryption_enabled = true
  transit_encryption_enabled = true
  auth_token                 = var.cache_auth_token

  automatic_failover_enabled = false
  multi_az_enabled           = false
  snapshot_retention_limit   = 0
  apply_immediately          = true
}
