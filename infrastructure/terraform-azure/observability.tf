# --- Supervision : Application Insights et alertes de la plateforme ---
# Les services .NET exportent traces, métriques et journaux vers Application Insights dès que
# APPLICATIONINSIGHTS_CONNECTION_STRING est posée (DevPulse.Observability, exportateur Azure Monitor OpenTelemetry).
# Ressource « basée sur un espace de travail » : les données sont rangées dans l'espace Log Analytics de main.tf
# (tables AppRequests, AppDependencies, AppTraces, AppMetrics…) et facturées comme lui. Personne n'a lancé
# `terraform plan` sur ce code : voir README.md, « Supervision ».

resource "azurerm_application_insights" "this" {
  count = var.enable_application_insights ? 1 : 0

  name                = "${local.name}-appi"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  workspace_id        = azurerm_log_analytics_workspace.this.id
  application_type    = "web"

  # Deux plafonds s'appliquent, le plus petit l'emporte (Microsoft Learn, « Set daily cap ») : celui-ci, et
  # daily_quota_gb de l'espace, qui compte aussi les journaux des conteneurs. Sans cet argument, azurerm pose 100 Go.
  daily_data_cap_in_gb                 = var.app_insights_daily_cap_gb
  daily_data_cap_notifications_enabled = true

  # Échantillonnage à l'ingestion : traces et journaux seulement, les métriques ne sont jamais échantillonnées.
  sampling_percentage = var.app_insights_sampling_percentage

  # Les tables App* gardent 90 jours sans frais de rétention (Microsoft Learn, « Manage data retention »).
  retention_in_days = 90

  # La chaîne de connexion contient la clé d'instrumentation : authentification locale nécessaire, les services
  # n'utilisent pas Entra ID pour l'export.
  local_authentication_enabled = true

  tags = local.tags

  lifecycle {
    precondition {
      condition     = var.app_insights_daily_cap_gb <= var.daily_quota_gb
      error_message = "app_insights_daily_cap_gb ne peut pas dépasser daily_quota_gb : le plafond de l'espace l'emporterait."
    }
  }
}

locals {
  app_insights_connection_string = try(azurerm_application_insights.this[0].connection_string, "")

  alert_frequency = "PT${var.platform_alert_frequency_minutes}M"

  # Règles sur les métriques OpenTelemetry des services, mêmes titres que les règles Grafana locales
  # (infrastructure/observability/alerting/devpulse-rules.yaml).
  #
  # Table interrogée : AppMetrics, car la portée des règles est l'espace Log Analytics. Depuis la ressource
  # Application Insights, la même donnée s'appelle customMetrics (name, valueSum, valueCount, customDimensions).
  # Correspondances (Microsoft Learn, migration vers une ressource basée sur un espace de travail) :
  # name => Name, valueSum => Sum, valueCount => ItemCount, valueMin/valueMax => Min/Max,
  # customDimensions => Properties, cloud_RoleName => AppRoleName. Name est le nom de l'instrument, points compris.
  #
  # Ce que produit l'exportateur : export en delta toutes les 60 s (lu dans son code source, branche principale :
  # TemporalityPreference = Delta, intervalle par défaut d'OpenTelemetry), donc pour un compteur Sum = hausse depuis
  # l'export précédent. Supposé, non vérifié sur des données réelles : une ligne par série (instrument et jeu
  # d'étiquettes) et par export ; jauge en dernière valeur (Sum) ; histogramme réduit à Sum, ItemCount, Min et Max,
  # sans les seaux, donc pas de percentile possible (Microsoft Learn).
  #
  # Pas de colonne de temps dans les résultats : une seule période d'évaluation par règle. Pas de découpage par
  # dimension : une série surveillée par règle, donc un seul prix par règle.
  platform_alerts = {
    "checks-stopped" = {
      display_name = "Plus aucune vérification"
      description  = "Le worker de monitoring ne vérifie plus rien alors qu'il en faisait dans l'heure : plus rien n'est surveillé. Se déclenche aussi quand la télémétrie n'arrive plus (plafond quotidien atteint)."
      severity     = 0
      window       = local.alert_frequency
      # La requête compare la dernière fenêtre à l'heure qui précède : elle doit voir une heure de données.
      time_range  = "PT1H"
      aggregation = "Count"
      measure     = null
      operator    = "GreaterThan"
      threshold   = 0

      query = <<-KQL
        let recent = toscalar(AppMetrics
            | where TimeGenerated > ago(${var.platform_alert_frequency_minutes}m) and Name == "devpulse.checks"
            | summarize sum(Sum));
        let earlier = toscalar(AppMetrics
            | where TimeGenerated between (ago(1h) .. ago(${var.platform_alert_frequency_minutes}m)) and Name == "devpulse.checks"
            | summarize sum(Sum));
        print Recent = coalesce(recent, 0.0), Earlier = coalesce(earlier, 0.0)
        | where Earlier > 0 and Recent == 0
        KQL
    }
    "dead-letters" = {
      display_name = "Message en file morte"
      description  = "Un événement a échoué après toutes ses reprises : un incident, une alerte ou une analyse manque. Voir les files .dlq dans RabbitMQ."
      severity     = 0
      window       = "PT15M"
      time_range   = null
      aggregation  = "Total"
      measure      = "DeadLettered"
      operator     = "GreaterThan"
      threshold    = 0

      query = <<-KQL
        AppMetrics
        | where Name == "devpulse.messaging.consumed" and tostring(Properties["outcome"]) == "dead_lettered"
        | summarize DeadLettered = sum(Sum)
        KQL
    }
    "outbox-stuck" = {
      display_name = "Outbox en retard"
      description  = "Un événement attend sa publication depuis plus d'une minute : le courtier est probablement injoignable."
      severity     = 0
      window       = local.alert_frequency
      time_range   = null
      aggregation  = "Maximum"
      measure      = "OldestAgeSeconds"
      operator     = "GreaterThan"
      threshold    = 60
      # Jauge : la valeur est dans Sum (une mesure par export).
      query = <<-KQL
        AppMetrics
        | where Name == "devpulse.outbox.oldest.age"
        | summarize OldestAgeSeconds = max(Sum)
        KQL
    }
    "notification-slow" = {
      display_name = "Délai de notification au-dessus de 10 s"
      description  = "Le délai moyen entre le fait signalé et la réception de l'alerte dépasse 10 s sur 15 minutes. Moyenne et non 95e centile : l'exportateur n'envoie pas les seaux de l'histogramme."
      severity     = 2
      window       = "PT15M"
      time_range   = null
      aggregation  = "Maximum"
      measure      = "AverageMs"
      operator     = "GreaterThan"
      threshold    = 10000
      # Moyenne pondérée : somme des durées sur nombre de mesures, toutes séries confondues.
      query = <<-KQL
        AppMetrics
        | where Name == "devpulse.alerts.notification.latency"
        | summarize TotalMs = sum(Sum), Notifications = sum(ItemCount)
        | where Notifications > 0
        | project AverageMs = TotalMs / Notifications
        KQL
    }
    "alerts-failed" = {
      display_name = "Alertes abandonnées"
      description  = "Un envoi d'alerte a échoué cinq fois : le destinataire n'a pas été prévenu."
      severity     = 0
      window       = "PT15M"
      time_range   = null
      aggregation  = "Total"
      measure      = "Failed"
      operator     = "GreaterThan"
      threshold    = 0

      query = <<-KQL
        AppMetrics
        | where Name == "devpulse.alerts.deliveries" and tostring(Properties["outcome"]) == "failed"
        | summarize Failed = sum(Sum)
        KQL
    }
  }
}

# Un seul destinataire : l'adresse du budget. Les courriels du groupe d'actions sont gratuits jusqu'à 1 000 par mois.
resource "azurerm_monitor_action_group" "platform" {
  count = var.enable_platform_alerts ? 1 : 0

  name                = "${local.name}-platform-alerts"
  resource_group_name = azurerm_resource_group.this.name
  # 12 caractères au plus : nom affiché comme expéditeur.
  short_name = "DevPulse"

  email_receiver {
    name                    = "exploitation"
    email_address           = var.budget_alert_email
    use_common_alert_schema = true
  }

  tags = local.tags

  lifecycle {
    precondition {
      condition     = var.enable_application_insights
      error_message = "enable_platform_alerts demande enable_application_insights = true : les règles lisent les métriques d'Application Insights. Sinon, passer enable_platform_alerts = false."
    }
  }
}

resource "azurerm_monitor_scheduled_query_rules_alert_v2" "platform" {
  for_each = { for key, rule in local.platform_alerts : key => rule if var.enable_platform_alerts }

  name                = "${local.name}-${each.key}"
  display_name        = each.value.display_name
  description         = each.value.description
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location

  scopes                    = [azurerm_log_analytics_workspace.this.id]
  severity                  = each.value.severity
  evaluation_frequency      = local.alert_frequency
  window_duration           = each.value.window
  query_time_range_override = each.value.time_range

  # Alerte à état : un courriel quand elle se déclenche, un autre quand elle se résout, rien entre les deux.
  auto_mitigation_enabled = true

  # La table AppMetrics n'existe qu'après les premières métriques reçues : sans cela, la création des règles
  # échouerait sur un espace neuf (comportement supposé, non vérifié).
  skip_query_validation = true

  criteria {
    query                   = each.value.query
    time_aggregation_method = each.value.aggregation
    metric_measure_column   = each.value.measure
    operator                = each.value.operator
    threshold               = each.value.threshold

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.platform[0].id]
  }

  tags = local.tags

  depends_on = [azurerm_application_insights.this]
}
