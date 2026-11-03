# Réseau virtuel (si enable_vnet) : il ne sert qu'à garder PostgreSQL sans accès public.
# Container Apps n'en a pas besoin pour l'entrée TCP interne de RabbitMQ : seule l'entrée TCP *externe* exige un
# réseau virtuel personnalisé (documentation Microsoft Learn, « Ingress in Azure Container Apps »).
#
# Le réseau lui-même est gratuit, mais un environnement Container Apps posé dans son propre réseau fait facturer
# un répartiteur de charge standard et deux IP publiques (entrée et sortie) dans le groupe géré par la plateforme.

locals {
  apps_subnet_cidr     = cidrsubnet(var.vnet_cidr, 8, 0) # /24 par défaut, au-delà du minimum /27
  postgres_subnet_cidr = cidrsubnet(var.vnet_cidr, 8, 1) # /24 par défaut, minimum /28
}

resource "azurerm_virtual_network" "this" {
  count = var.enable_vnet ? 1 : 0

  name                = "${local.name}-vnet"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  address_space       = [var.vnet_cidr]
  tags                = local.tags
}

# Sous-réseau de l'environnement Container Apps (profils de charge de travail : délégué, /27 au minimum).
# Les réplicas y prennent leurs adresses : la passerelle fait confiance à cette plage pour X-Forwarded-For.
resource "azurerm_subnet" "apps" {
  count = var.enable_vnet ? 1 : 0

  name                 = "container-apps"
  resource_group_name  = azurerm_resource_group.this.name
  virtual_network_name = azurerm_virtual_network.this[0].name
  address_prefixes     = [local.apps_subnet_cidr]

  delegation {
    name = "container-apps"

    service_delegation {
      name    = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

# Sous-réseau délégué au serveur flexible : aucune autre ressource ne peut y être placée.
resource "azurerm_subnet" "postgres" {
  count = var.enable_vnet ? 1 : 0

  name                 = "postgres"
  resource_group_name  = azurerm_resource_group.this.name
  virtual_network_name = azurerm_virtual_network.this[0].name
  address_prefixes     = [local.postgres_subnet_cidr]

  delegation {
    name = "postgres"

    service_delegation {
      name    = "Microsoft.DBforPostgreSQL/flexibleServers"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }

  # Azure ajoute lui-même le point de terminaison de service Microsoft.Storage à la création du premier serveur
  # (envoi des WAL) et prévient qu'il ne faut pas le retirer : Terraform ne doit pas le voir comme un écart.
  lifecycle {
    ignore_changes = [service_endpoint]
  }
}

# Zone DNS privée du serveur : le nom doit finir par .postgres.database.azure.com et ne pas être celui du
# serveur (forme <nom>.<nom>.postgres.database.azure.com acceptée). privatelink.postgres.database.azure.com sert
# aux points de terminaison privés, pas à l'intégration par sous-réseau délégué.
resource "azurerm_private_dns_zone" "postgres" {
  count = var.enable_vnet ? 1 : 0

  name                = "${local.name}.private.postgres.database.azure.com"
  resource_group_name = azurerm_resource_group.this.name
  tags                = local.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "postgres" {
  count = var.enable_vnet ? 1 : 0

  name                = "${local.name}-vnet"
  private_dns_zone_id = azurerm_private_dns_zone.postgres[0].id
  virtual_network_id  = azurerm_virtual_network.this[0].id
  tags                = local.tags
}
