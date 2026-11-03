output "db_address" {
  description = "Nom d'hôte de l'instance PostgreSQL."
  value       = aws_db_instance.this.address
}

output "mq_host" {
  description = "Nom d'hôte du broker, extrait de « amqps://<hôte>:5671 »."
  value       = split(":", trimprefix(aws_mq_broker.this.instances[0].endpoints[0], "amqps://"))[0]
}

output "cache_endpoint" {
  description = "Point d'accès principal de Valkey."
  value       = aws_elasticache_replication_group.this.primary_endpoint_address
}
