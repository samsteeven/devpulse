variable "name" {
  description = "Nom du service, du conteneur et de l'enregistrement Cloud Map."
  type        = string
}

variable "name_prefix" {
  description = "Préfixe des noms de ressources (projet-environnement)."
  type        = string
}

variable "region" {
  description = "Région des journaux CloudWatch."
  type        = string
}

variable "cluster_arn" {
  type = string
}

variable "image" {
  description = "Image complète, dépôt et tag."
  type        = string
}

variable "ecr_repository_arn" {
  description = "Dépôt ECR de l'image, seul dépôt que le rôle d'exécution peut lire."
  type        = string
}

variable "cpu" {
  type = number
}

variable "memory" {
  type = number
}

variable "cpu_architecture" {
  type = string
}

variable "desired_count" {
  type = number
}

variable "capacity_provider" {
  description = "FARGATE ou FARGATE_SPOT."
  type        = string

  validation {
    condition     = contains(["FARGATE", "FARGATE_SPOT"], var.capacity_provider)
    error_message = "capacity_provider : FARGATE ou FARGATE_SPOT."
  }
}

variable "container_port" {
  description = "Port d'écoute du conteneur. Null pour un worker sans port."
  type        = number
  default     = null
}

variable "environment" {
  description = "Variables d'environnement non sensibles."
  type        = map(string)
  default     = {}
}

variable "secrets" {
  description = "Variables lues dans Secrets Manager au démarrage (nom => ARN, ou ARN:clé-json:: pour une clé d'un secret JSON)."
  type        = map(string)
  default     = {}
}

variable "secret_arns" {
  description = "ARN des secrets lisibles par le rôle d'exécution (sans suffixe de clé JSON)."
  type        = list(string)
  default     = []
}

variable "task_role_arn" {
  type = string
}

variable "subnet_ids" {
  type = list(string)
}

variable "security_group_ids" {
  type = list(string)
}

variable "assign_public_ip" {
  type = bool
}

# Booléens séparés plutôt qu'un test « ARN non nul » : un `count` ou un `for_each` doit être connu au plan, alors
# que les ARN ne le sont qu'après création.
variable "attach_load_balancer" {
  type    = bool
  default = false
}

variable "target_group_arn" {
  type    = string
  default = null
}

variable "enable_service_discovery" {
  type    = bool
  default = false
}

variable "service_discovery_namespace_id" {
  type    = string
  default = null
}

variable "log_retention_days" {
  type = number
}
