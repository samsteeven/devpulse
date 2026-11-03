output "service_name" {
  value = aws_ecs_service.this.name
}

output "log_group_name" {
  value = aws_cloudwatch_log_group.this.name
}

output "execution_role_arn" {
  value = aws_iam_role.execution.arn
}
