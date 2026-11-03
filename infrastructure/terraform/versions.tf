terraform {
  required_version = ">= 1.9"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
    tls = {
      source  = "hashicorp/tls"
      version = "~> 4.0"
    }
  }

  # L'état contient des secrets en clair (clé privée JWT, mots de passe) : il ne doit pas rester sur un poste.
  # Exemple de backend chiffré, à créer avant le premier `terraform init` (bucket versionné, clé KMS dédiée).
  # `use_lockfile` demande Terraform 1.10 ou plus ; avant, utiliser une table DynamoDB (`dynamodb_table`).
  #
  # backend "s3" {
  #   bucket       = "devpulse-tfstate-<compte>"
  #   key          = "devpulse/demo/terraform.tfstate"
  #   region       = "eu-west-3"
  #   encrypt      = true
  #   kms_key_id   = "arn:aws:kms:eu-west-3:<compte>:key/<id>"
  #   use_lockfile = true
  # }
}

provider "aws" {
  region = var.aws_region

  default_tags {
    tags = {
      Project     = var.project
      Environment = var.environment
      ManagedBy   = "terraform"
    }
  }
}
