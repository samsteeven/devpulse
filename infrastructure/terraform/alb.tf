# ALB public : /api/* et /hubs/* vers la passerelle, le reste vers le front.
#
# Sans `certificate_arn`, l'ALB ne sert que du HTTP : jetons et mots de passe circulent en clair. À réserver à une
# démonstration courte, sans vraies données. Le cookie de rafraîchissement d'Identity perd alors l'attribut Secure
# (un navigateur ne renvoie pas un cookie Secure sur http://, hors localhost) : voir ecs.tf.

resource "aws_lb" "this" {
  name               = "${local.name}-alb"
  load_balancer_type = "application"
  internal           = false
  security_groups    = [aws_security_group.alb.id]
  subnets            = module.network.public_subnet_ids

  # SignalR passe par WebSocket ; les pings du hub (15 s par défaut) gardent la connexion bien en dessous de 120 s.
  idle_timeout = 120

  drop_invalid_header_fields = true
}

resource "aws_lb_target_group" "gateway" {
  name        = "${local.name}-gateway"
  target_type = "ip"
  protocol    = "HTTP"
  port        = 8080
  vpc_id      = module.network.vpc_id

  deregistration_delay = 30

  health_check {
    path                = "/health/live"
    matcher             = "200"
    interval            = 15
    timeout             = 5
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }

  # Garde un navigateur sur la même instance de passerelle. L'affinité qui compte pour SignalR (vers la même
  # instance d'Alerting) est assurée par YARP (cookie .DevPulse.Affinity) ; celle-ci n'a d'effet qu'à partir de deux
  # passerelles.
  stickiness {
    type            = "lb_cookie"
    enabled         = true
    cookie_duration = 3600
  }
}

resource "aws_lb_target_group" "web" {
  name        = "${local.name}-web"
  target_type = "ip"
  protocol    = "HTTP"
  port        = 8080
  vpc_id      = module.network.vpc_id

  deregistration_delay = 30

  health_check {
    path                = "/"
    matcher             = "200"
    interval            = 15
    timeout             = 5
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

locals {
  target_group_arns = {
    gateway = aws_lb_target_group.gateway.arn
    web     = aws_lb_target_group.web.arn
  }
}

# Avec certificat : HTTP redirige vers HTTPS. Sans certificat : HTTP sert l'application.
resource "aws_lb_listener" "http" {
  load_balancer_arn = aws_lb.this.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type             = local.https_enabled ? "redirect" : "forward"
    target_group_arn = local.https_enabled ? null : aws_lb_target_group.web.arn

    dynamic "redirect" {
      for_each = local.https_enabled ? [1] : []

      content {
        port        = "443"
        protocol    = "HTTPS"
        status_code = "HTTP_301"
      }
    }
  }
}

resource "aws_lb_listener" "https" {
  count = local.https_enabled ? 1 : 0

  load_balancer_arn = aws_lb.this.arn
  port              = 443
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = var.certificate_arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.web.arn
  }
}

resource "aws_lb_listener_rule" "gateway" {
  listener_arn = local.https_enabled ? aws_lb_listener.https[0].arn : aws_lb_listener.http.arn
  priority     = 10

  action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.gateway.arn
  }

  condition {
    path_pattern {
      values = ["/api/*", "/hubs/*"]
    }
  }
}
