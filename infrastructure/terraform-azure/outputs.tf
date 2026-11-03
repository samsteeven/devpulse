output "app_url" {
  description = "Adresse publique de l'application (HTTPS, certificat géré par Container Apps)."
  value       = "https://${azurerm_container_app.app["web"].ingress[0].fqdn}"
}

output "resource_group_name" {
  value = azurerm_resource_group.this.name
}

output "postgres_fqdn" {
  description = "Nom du serveur PostgreSQL. Avec enable_vnet, il ne se résout que depuis le réseau virtuel."
  value       = azurerm_postgresql_flexible_server.this.fqdn
}

output "postgres_server_name" {
  description = "Pour `az postgres flexible-server stop --name …`."
  value       = azurerm_postgresql_flexible_server.this.name
}

output "container_app_environment_domain" {
  description = "Domaine par défaut de l'environnement (les applications internes sont sous <nom>.internal.<domaine>)."
  value       = azurerm_container_app_environment.this.default_domain
}

output "log_analytics_workspace_id" {
  description = "Identifiant de l'espace de travail, pour `az monitor log-analytics query --workspace`."
  value       = azurerm_log_analytics_workspace.this.workspace_id
}

output "application_insights_name" {
  description = "Ressource Application Insights (null si enable_application_insights = false). Portail : Application Insights > ce nom > Application map, Transaction search, Logs (tables classiques customMetrics, requests…) ; ou l'espace Log Analytics > Logs (tables AppMetrics, AppRequests…)."
  value       = try(azurerm_application_insights.this[0].name, null)
}

output "platform_alert_rules" {
  description = "Noms des règles d'alerte de la plateforme (Monitor > Alerts > Alert rules), vide si enable_platform_alerts = false."
  value       = [for rule in azurerm_monitor_scheduled_query_rules_alert_v2.platform : rule.name]
}
