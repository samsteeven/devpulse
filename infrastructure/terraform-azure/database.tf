# Un serveur PostgreSQL Flexible Server, une base par service, un seul utilisateur (l'administrateur).
# Avec enable_vnet : accès privé par le sous-réseau délégué, aucun point d'accès public.
# Sans : accès public limité par la règle « services Azure » (0.0.0.0), c'est-à-dire toute adresse Azure, y
# compris celles d'autres clients ; le mot de passe aléatoire et TLS obligatoire restent les seules protections.

resource "azurerm_postgresql_flexible_server" "this" {
  name                = "${local.name}-pg-${random_string.suffix.result}"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name

  version    = var.db_version
  sku_name   = var.db_sku
  storage_mb = var.db_storage_mb

  administrator_login    = var.db_admin_login
  administrator_password = random_password.db.result

  backup_retention_days        = var.db_backup_retention_days
  geo_redundant_backup_enabled = false

  delegated_subnet_id           = var.enable_vnet ? azurerm_subnet.postgres[0].id : null
  private_dns_zone_id           = var.enable_vnet ? azurerm_private_dns_zone.postgres[0].id : null
  public_network_access_enabled = !var.enable_vnet

  tags = local.tags

  # Azure choisit la zone de disponibilité si on ne la fixe pas : sans cela, chaque plan proposerait de la changer.
  lifecycle {
    ignore_changes = [zone]
  }

  # La résolution du nom privé suppose la zone DNS liée au réseau avant la création du serveur.
  depends_on = [azurerm_private_dns_zone_virtual_network_link.postgres]
}

resource "azurerm_postgresql_flexible_server_firewall_rule" "azure_services" {
  count = var.enable_vnet ? 0 : 1

  name             = "AllowAzureServices"
  server_id        = azurerm_postgresql_flexible_server.this.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

# Bases créées ici plutôt que par la première migration : le script local (01-create-databases.sql) fait de même.
# Pas de prevent_destroy : `terraform destroy` doit pouvoir tout supprimer après une démonstration.
resource "azurerm_postgresql_flexible_server_database" "this" {
  for_each = local.databases

  name      = each.value
  server_id = azurerm_postgresql_flexible_server.this.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

locals {
  # SSL Mode=Require chiffre sans vérifier le certificat (comme la version AWS) ; Azure impose TLS par défaut
  # (require_secure_transport). VerifyFull demanderait les certificats racine d'Azure dans les images.
  connection_strings = {
    for key, db in local.databases :
    key => "Host=${azurerm_postgresql_flexible_server.this.fqdn};Port=5432;Database=${db};Username=${var.db_admin_login};Password=${random_password.db.result};SSL Mode=Require"
  }
}
