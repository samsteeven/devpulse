# Groupes de sécurité des tâches. Sans NAT, les tâches ont une IP publique : ce sont ces règles, et elles seules,
# qui les ferment à Internet. Aucune n'accepte d'entrée depuis 0.0.0.0/0.
#
#   Internet -> ALB (80, 443) -> gateway, web (8080)
#   gateway, workers -> API internes (8080)
#   API internes, workers -> PostgreSQL, Amazon MQ ; Alerting -> Valkey (module data)

resource "aws_security_group" "alb" {
  name        = "${local.name}-alb"
  description = "ALB public"
  vpc_id      = module.network.vpc_id
}

resource "aws_security_group" "gateway" {
  name        = "${local.name}-gateway"
  description = "Passerelle YARP : entree depuis ALB"
  vpc_id      = module.network.vpc_id
}

resource "aws_security_group" "web" {
  name        = "${local.name}-web"
  description = "Front nginx : entree depuis ALB"
  vpc_id      = module.network.vpc_id
}

resource "aws_security_group" "internal" {
  name        = "${local.name}-internal"
  description = "API internes : entree depuis la passerelle et les workers"
  vpc_id      = module.network.vpc_id
}

resource "aws_security_group" "workers" {
  name        = "${local.name}-workers"
  description = "Workers : aucune entree"
  vpc_id      = module.network.vpc_id
}

# --- ALB ---

resource "aws_vpc_security_group_ingress_rule" "alb" {
  for_each = { for pair in setproduct(["80", "443"], var.allowed_ingress_cidrs) : "${pair[0]}-${pair[1]}" => pair }

  security_group_id = aws_security_group.alb.id
  cidr_ipv4         = each.value[1]
  ip_protocol       = "tcp"
  from_port         = tonumber(each.value[0])
  to_port           = tonumber(each.value[0])
  description       = "HTTP(S) public"
}

resource "aws_vpc_security_group_egress_rule" "alb_to_targets" {
  for_each = {
    gateway = aws_security_group.gateway.id
    web     = aws_security_group.web.id
  }

  security_group_id            = aws_security_group.alb.id
  referenced_security_group_id = each.value
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
  description                  = "Vers ${each.key}"
}

# --- Entrées des tâches ---

resource "aws_vpc_security_group_ingress_rule" "gateway_from_alb" {
  security_group_id            = aws_security_group.gateway.id
  referenced_security_group_id = aws_security_group.alb.id
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
  description                  = "Depuis ALB"
}

resource "aws_vpc_security_group_ingress_rule" "web_from_alb" {
  security_group_id            = aws_security_group.web.id
  referenced_security_group_id = aws_security_group.alb.id
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
  description                  = "Depuis ALB"
}

# monitoring-worker appelle Registry (/internal/services/active) ; ai-worker n'appelle aucune API mais partage ce
# groupe. Un groupe par worker serait l'étape suivante.
resource "aws_vpc_security_group_ingress_rule" "internal" {
  for_each = {
    gateway = aws_security_group.gateway.id
    workers = aws_security_group.workers.id
  }

  security_group_id            = aws_security_group.internal.id
  referenced_security_group_id = each.value
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
  description                  = "Depuis ${each.key}"
}

# --- Sorties des tâches ---
# Ouvertes : ECR, Secrets Manager et CloudWatch sont joints par leurs points d'accès publics (pas de points de
# terminaison VPC, facturés à l'heure et par zone), et les workers sondent des URL arbitraires. La protection
# SSRF applicative (TargetPolicy) empêche de viser une adresse interne.

resource "aws_vpc_security_group_egress_rule" "tasks_all" {
  for_each = {
    gateway  = aws_security_group.gateway.id
    web      = aws_security_group.web.id
    internal = aws_security_group.internal.id
    workers  = aws_security_group.workers.id
  }

  security_group_id = each.value
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "-1"
  description       = "Sortie ouverte"
}
