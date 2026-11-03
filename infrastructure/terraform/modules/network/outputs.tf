output "vpc_id" {
  value = aws_vpc.this.id
}

output "public_subnet_ids" {
  value = aws_subnet.public[*].id
}

output "private_subnet_ids" {
  value = aws_subnet.private[*].id
}

output "task_subnet_ids" {
  description = "Sous-réseaux des tâches : privés avec NAT, publics sans."
  value       = var.enable_nat_gateway ? aws_subnet.private[*].id : aws_subnet.public[*].id
}

output "assign_public_ip" {
  description = "Vrai quand les tâches doivent recevoir une IP publique (pas de NAT)."
  value       = !var.enable_nat_gateway
}
