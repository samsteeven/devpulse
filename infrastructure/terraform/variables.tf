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
    error_message = "environment : 2 à 12 caractères parmi a-z, 0-9 et « - » (les noms ALB et groupes cibles sont limités à 32 caractères)."
  }
}

variable "aws_region" {
  description = "Région AWS."
  type        = string
  default     = "eu-west-3"
}

variable "vpc_cidr" {
  description = "Plage du VPC. Elle sert aussi de réseau de confiance pour X-Forwarded-For dans la passerelle."
  type        = string
  default     = "10.40.0.0/16"
}

variable "enable_nat_gateway" {
  description = "Faux : tâches en sous-réseaux publics avec IP publique, sans NAT. Vrai : tâches en sous-réseaux privés derrière une seule passerelle NAT. Écart de coût faible avec 9 tâches (voir docs/architecture/cout-aws.md)."
  type        = bool
  default     = false
}

variable "certificate_arn" {
  description = "Certificat ACM (même région) pour l'écouteur HTTPS. Vide : HTTP seul, acceptable uniquement pour une démonstration courte."
  type        = string
  default     = ""
}

variable "allowed_ingress_cidrs" {
  description = "Plages autorisées à joindre l'ALB (ports 80 et 443)."
  type        = list(string)
  default     = ["0.0.0.0/0"]
}

variable "image_tag" {
  description = "Tag des images dans ECR, le même pour tous les services (par exemple le SHA du commit)."
  type        = string

  validation {
    condition     = length(var.image_tag) > 0
    error_message = "image_tag est obligatoire."
  }
}

variable "cpu_architecture" {
  description = "Architecture des tâches : X86_64, ou ARM64 (environ 20 % moins cher) si les images sont construites pour arm64."
  type        = string
  default     = "X86_64"

  validation {
    condition     = contains(["X86_64", "ARM64"], var.cpu_architecture)
    error_message = "cpu_architecture : X86_64 ou ARM64."
  }
}

variable "service_resources" {
  description = "CPU (unités, 1024 = 1 vCPU) et mémoire (Mio) par service. Un service absent prend 256/512."
  type = map(object({
    cpu    = number
    memory = number
  }))
  default = {}
}

variable "desired_counts" {
  description = "Nombre de tâches par service. Un service absent en a 1. Tout à 0 arrête le calcul sans détruire les données."
  type        = map(number)
  default     = {}
}

variable "use_spot_for_workers" {
  description = "Place monitoring-worker et ai-worker sur Fargate Spot (moins cher, interruptible avec deux minutes de préavis)."
  type        = bool
  default     = false
}

variable "container_insights" {
  description = "Active Container Insights sur le cluster (métriques facturées en plus)."
  type        = bool
  default     = false
}

variable "log_retention_days" {
  description = "Rétention des journaux CloudWatch, en jours."
  type        = number
  default     = 14

  validation {
    condition     = contains([1, 3, 5, 7, 14, 30, 60, 90, 120, 150, 180, 365], var.log_retention_days)
    error_message = "log_retention_days doit être une valeur acceptée par CloudWatch Logs (1, 3, 5, 7, 14, 30, 60, 90…)."
  }
}

variable "ecr_force_delete" {
  description = "Permet à `terraform destroy` de supprimer les dépôts ECR même s'ils contiennent des images."
  type        = bool
  default     = true
}

variable "secret_recovery_window_days" {
  description = "Délai avant suppression définitive d'un secret. 0 permet de détruire puis recréer l'environnement avec les mêmes noms."
  type        = number
  default     = 0
}

# --- PostgreSQL ---

variable "db_instance_class" {
  description = "Classe de l'instance RDS PostgreSQL."
  type        = string
  default     = "db.t4g.micro"
}

variable "db_engine_version" {
  description = "Version de PostgreSQL. Une version majeure seule laisse RDS choisir la mineure."
  type        = string
  default     = "17"
}

variable "db_allocated_storage" {
  description = "Stockage gp3 alloué, en Go."
  type        = number
  default     = 20
}

variable "db_username" {
  description = "Utilisateur maître. Les services l'utilisent tous (simplification, voir README)."
  type        = string
  default     = "devpulse"
}

variable "db_backup_retention_days" {
  description = "Rétention des sauvegardes automatiques RDS, en jours (0 les désactive)."
  type        = number
  default     = 7
}

variable "db_deletion_protection" {
  description = "Empêche la suppression de l'instance RDS. Faux par défaut pour pouvoir détruire la démonstration."
  type        = bool
  default     = false
}

variable "db_skip_final_snapshot" {
  description = "Vrai : pas d'instantané final à la destruction (démonstration). Faux pour garder les données."
  type        = bool
  default     = true
}

# --- Amazon MQ ---

variable "mq_instance_type" {
  description = "Type d'instance Amazon MQ."
  type        = string
  default     = "mq.t3.micro"
}

variable "mq_engine_version" {
  description = "Version de RabbitMQ. 3.13 est la seule disponible sur mq.t3.micro ; 4.2 demande mq.m7g."
  type        = string
  default     = "3.13"
}

variable "mq_username" {
  description = "Utilisateur RabbitMQ des services."
  type        = string
  default     = "devpulse"
}

# --- ElastiCache ---

variable "cache_node_type" {
  description = "Type de nœud ElastiCache (Valkey)."
  type        = string
  default     = "cache.t4g.micro"
}

variable "cache_engine_version" {
  description = "Version de Valkey."
  type        = string
  default     = "8.0"
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
