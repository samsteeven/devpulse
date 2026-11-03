# Budget mensuel sur l'abonnement entier, pas seulement sur le groupe de ressources : le répartiteur et les IP
# publiques de l'environnement Container Apps sont dans un groupe géré par la plateforme (main.tf).
#
# Un budget n'arrête rien : il envoie des courriels. Les coûts arrivent dans Cost Management avec un retard
# pouvant atteindre 72 heures sur les abonnements à l'usage. Sur Azure for Students, le vrai garde-fou reste le
# crédit : épuisé, l'abonnement et ses ressources sont désactivés.
#
# À vérifier au premier apply : la page « Understand Cost Management data » range encore Azure for Students
# (MS-AZR-0170P) parmi les offres non prises en charge par Cost Management, alors que la page Education Hub
# recommande d'y créer des budgets. Si Azure refuse la création, passer enable_budget = false (voir README).

data "azurerm_subscription" "current" {}

resource "azurerm_consumption_budget_subscription" "this" {
  count = var.enable_budget ? 1 : 0

  name            = "${local.name}-budget"
  subscription_id = data.azurerm_subscription.current.id
  amount          = var.monthly_budget
  time_grain      = "Monthly"

  # Premier jour du mois courant (exigé par l'API). La date change chaque mois mais forcerait un remplacement :
  # elle est figée à la création.
  time_period {
    start_date = formatdate("YYYY-MM-01'T'00:00:00Z", timestamp())
  }

  dynamic "notification" {
    for_each = {
      "actual-50"      = { threshold = 50, type = "Actual" }
      "actual-80"      = { threshold = 80, type = "Actual" }
      "actual-100"     = { threshold = 100, type = "Actual" }
      "forecasted-100" = { threshold = 100, type = "Forecasted" }
    }

    content {
      enabled        = true
      threshold      = notification.value.threshold
      threshold_type = notification.value.type
      operator       = "GreaterThanOrEqualTo"
      contact_emails = [var.budget_alert_email]
    }
  }

  lifecycle {
    ignore_changes = [time_period]
  }
}
