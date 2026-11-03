variable "name" {
  description = "Préfixe des noms de ressources."
  type        = string
}

variable "vpc_id" {
  type = string
}

variable "private_subnet_ids" {
  type = list(string)
}

# Cartes à clés fixes (et non listes) : `for_each` exige des clés connues au moment du plan, alors que les
# identifiants des groupes de sécurité ne le sont qu'après leur création.
variable "db_client_security_group_ids" {
  description = "Groupes de sécurité autorisés à joindre PostgreSQL (nom => identifiant)."
  type        = map(string)
}

variable "mq_client_security_group_ids" {
  description = "Groupes de sécurité autorisés à joindre Amazon MQ (nom => identifiant)."
  type        = map(string)
}

variable "cache_client_security_group_ids" {
  description = "Groupes de sécurité autorisés à joindre Valkey (nom => identifiant)."
  type        = map(string)
}

variable "db_instance_class" {
  type = string
}

variable "db_engine_version" {
  type = string
}

variable "db_allocated_storage" {
  type = number
}

variable "db_username" {
  type = string
}

variable "db_password" {
  type      = string
  sensitive = true
}

variable "db_backup_retention_days" {
  type = number
}

variable "db_deletion_protection" {
  type = bool
}

variable "db_skip_final_snapshot" {
  type = bool
}

variable "mq_instance_type" {
  type = string
}

variable "mq_engine_version" {
  type = string
}

variable "mq_username" {
  type = string
}

variable "mq_password" {
  type      = string
  sensitive = true
}

variable "cache_node_type" {
  type = string
}

variable "cache_engine_version" {
  type = string
}

variable "cache_auth_token" {
  type      = string
  sensitive = true
}
