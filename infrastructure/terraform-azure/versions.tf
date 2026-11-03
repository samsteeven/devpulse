terraform {
  required_version = ">= 1.9"

  required_providers {
    # 5.x est la version majeure courante du fournisseur (5.12.0 au 29/10/2026 sur le registre Terraform).
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.0"
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

  # L'état contient des secrets en clair (clé privée JWT, mots de passe, chaînes de connexion) : il ne doit pas
  # rester sur un poste. Exemple de backend dans un compte de stockage (à créer avant le premier `terraform init`,
  # chiffré par défaut, accès par Entra ID plutôt que par clé de compte).
  #
  # backend "azurerm" {
  #   resource_group_name  = "devpulse-tfstate"
  #   storage_account_name = "devpulsetfstate<suffixe>"
  #   container_name       = "tfstate"
  #   key                  = "devpulse/demo/terraform.tfstate"
  #   use_azuread_auth     = true
  # }
}

provider "azurerm" {
  features {
    # Azure crée de lui-même, avec Application Insights, une règle de détection intelligente « Failure Anomalies »
    # (et son groupe d'actions) qui écrit aux propriétaires de l'abonnement. Elle est désactivée à la création : les
    # règles d'observability.tf la remplacent.
    application_insights {
      disable_generated_rule = true
    }

    # Ces ressources créées par Azure ne sont pas dans l'état Terraform : sans cela, `terraform destroy` refuserait de
    # supprimer le groupe de ressources qui les contient. Le groupe ne sert qu'à ce déploiement.
    resource_group {
      prevent_deletion_if_contains_resources = false
    }
  }

  # Vide : abonnement de ARM_SUBSCRIPTION_ID, sinon abonnement par défaut d'Azure CLI (`az account set`).
  subscription_id = var.subscription_id

  # Depuis azurerm 5.0, aucun fournisseur de ressources n'est enregistré par défaut (`none`). Un abonnement neuf,
  # comme Azure for Students, n'a souvent ni Microsoft.App ni Microsoft.DBforPostgreSQL : on enregistre la liste
  # utile, et seulement elle. Il faut le droit `*/register/action` sur l'abonnement (propriétaire ou contributeur).
  resource_provider_registrations = "none"
  resource_providers_to_register  = var.register_resource_providers ? local.resource_providers : []
}

locals {
  resource_providers = [
    "Microsoft.App",                 # Container Apps
    "Microsoft.OperationalInsights", # Log Analytics
    "Microsoft.Insights",            # Application Insights, groupe d'actions, règles d'alerte
    "Microsoft.DBforPostgreSQL",     # PostgreSQL Flexible Server
    "Microsoft.Network",             # réseau virtuel, zone DNS privée
    "Microsoft.Consumption",         # budget
    "Microsoft.Cache",               # Azure Managed Redis, seulement si enable_redis
  ]
}
