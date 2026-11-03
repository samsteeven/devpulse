output "alb_dns_name" {
  description = "Nom DNS de l'ALB. Avec un certificat, créer un enregistrement CNAME ou alias vers ce nom."
  value       = aws_lb.this.dns_name
}

output "app_url" {
  description = "Adresse de l'application (HTTP seul si aucun certificat n'est fourni)."
  value       = local.https_enabled ? "https://${aws_lb.this.dns_name}" : "http://${aws_lb.this.dns_name}"
}

output "ecr_repository_urls" {
  description = "Dépôt ECR de chaque service, pour `docker push <url>:<image_tag>`."
  value       = { for name, repository in aws_ecr_repository.this : name => repository.repository_url }
}

output "ecs_cluster_name" {
  value = aws_ecs_cluster.this.name
}

output "log_groups" {
  description = "Groupe de journaux CloudWatch de chaque service."
  value       = { for name, service in module.service : name => service.log_group_name }
}
