# Secrets générés par Terraform et rangés dans Secrets Manager. Ils sont aussi dans l'état Terraform, en clair :
# l'état doit être chiffré et d'accès restreint (exemple de backend S3 + KMS dans versions.tf).

# Caractères alphanumériques seulement : ces valeurs entrent dans des chaînes de connexion où « ; », « = », « , »,
# « @ » ou « / » auraient un sens.
resource "random_password" "db" {
  length  = 32
  special = false
}

resource "random_password" "mq" {
  length  = 32
  special = false
}

resource "random_password" "cache" {
  length  = 32
  special = false
}

resource "random_password" "internal_api_key" {
  length  = 64
  special = false
}

# --- Clés JWT (RS256) ---

resource "tls_private_key" "jwt" {
  algorithm = "RSA"
  rsa_bits  = 2048
}

locals {
  # Les services attendent le base64 du DER : PKCS#8 pour la clé privée (ImportPkcs8PrivateKey), SubjectPublicKeyInfo
  # pour la clé publique (ImportSubjectPublicKeyInfo), comme le produit make-env.ps1.
  # Un PEM n'est que ce base64, coupé à 64 colonnes, entre une ligne « -----BEGIN …----- » et une ligne
  # « -----END … ----- ». Retirer ces deux lignes et tous les blancs redonne exactement le base64 du DER.
  # `private_key_pem` est en PKCS#1 pour RSA : il faut `private_key_pem_pkcs8`. `public_key_pem` est déjà en SPKI.
  jwt_private_key_b64 = replace(tls_private_key.jwt.private_key_pem_pkcs8, "/-----[A-Z ]+-----|\\s/", "")
  jwt_public_key_b64  = replace(tls_private_key.jwt.public_key_pem, "/-----[A-Z ]+-----|\\s/", "")
}

# Garde-fou évalué à chaque plan : base64 sans en-tête ni blanc, et bonnes structures DER. Une clé RSA 2048
# (exposant 65537) en SPKI commence toujours par le préfixe ci-dessous ; une clé PKCS#8 contient l'OID rsaEncryption
# (« BgkqhkiG9w0BAQEF ») dans ses premiers octets, une clé PKCS#1 non.
check "jwt_key_format" {
  assert {
    condition     = can(regex("^[A-Za-z0-9+/]+={0,2}$", local.jwt_public_key_b64)) && startswith(local.jwt_public_key_b64, "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA")
    error_message = "La clé publique JWT n'est pas le base64 d'un SubjectPublicKeyInfo RSA 2048."
  }

  assert {
    condition     = nonsensitive(can(regex("^[A-Za-z0-9+/]+={0,2}$", local.jwt_private_key_b64)) && strcontains(substr(local.jwt_private_key_b64, 0, 48), "BgkqhkiG9w0BAQEF"))
    error_message = "La clé privée JWT n'est pas le base64 d'une clé PKCS#8 RSA."
  }
}

# --- Secrets Manager ---

resource "aws_secretsmanager_secret" "jwt_private_key" {
  name                    = "${local.name}/jwt-private-key"
  description             = "Cle privee RS256 d'Identity (base64 PKCS#8 DER)"
  recovery_window_in_days = var.secret_recovery_window_days
}

resource "aws_secretsmanager_secret_version" "jwt_private_key" {
  secret_id     = aws_secretsmanager_secret.jwt_private_key.id
  secret_string = local.jwt_private_key_b64
}

resource "aws_secretsmanager_secret" "internal_api_key" {
  name                    = "${local.name}/internal-api-key"
  description             = "Cle d'appel interne monitoring-worker vers Registry"
  recovery_window_in_days = var.secret_recovery_window_days
}

resource "aws_secretsmanager_secret_version" "internal_api_key" {
  secret_id     = aws_secretsmanager_secret.internal_api_key.id
  secret_string = random_password.internal_api_key.result
}

# Un secret JSON pour PostgreSQL : le mot de passe maître et une chaîne de connexion par base. Chaque service lit
# sa clé (ARN:clé::) ; un secret par base coûterait 0,40 $/mois de plus chacun.
resource "aws_secretsmanager_secret" "database" {
  name                    = "${local.name}/database"
  description             = "PostgreSQL : mot de passe maitre et chaines de connexion par service"
  recovery_window_in_days = var.secret_recovery_window_days
}

resource "aws_secretsmanager_secret_version" "database" {
  secret_id = aws_secretsmanager_secret.database.id

  secret_string = jsonencode(merge(
    {
      host     = module.data.db_address
      username = var.db_username
      password = random_password.db.result
    },
    {
      for key, db in local.databases :
      key => "Host=${module.data.db_address};Port=5432;Database=${db};Username=${var.db_username};Password=${random_password.db.result};SSL Mode=Require"
    },
  ))
}

resource "aws_secretsmanager_secret" "rabbitmq" {
  name                    = "${local.name}/rabbitmq-password"
  description             = "Mot de passe RabbitMQ des services"
  recovery_window_in_days = var.secret_recovery_window_days
}

resource "aws_secretsmanager_secret_version" "rabbitmq" {
  secret_id     = aws_secretsmanager_secret.rabbitmq.id
  secret_string = random_password.mq.result
}

resource "aws_secretsmanager_secret" "redis" {
  name                    = "${local.name}/redis"
  description             = "Jeton Valkey et chaine de connexion StackExchange.Redis"
  recovery_window_in_days = var.secret_recovery_window_days
}

resource "aws_secretsmanager_secret_version" "redis" {
  secret_id = aws_secretsmanager_secret.redis.id

  secret_string = jsonencode({
    auth_token        = random_password.cache.result
    connection_string = "${module.data.cache_endpoint}:6379,ssl=true,password=${random_password.cache.result},abortConnect=false"
  })
}

# Facultatif : créé seulement si une clé est fournie (fournisseur Llm).
# `nonsensitive` porte sur le booléen « clé fournie ou non », pas sur la clé.
locals {
  ai_api_key_enabled = nonsensitive(var.ai_api_key != "")
}

resource "aws_secretsmanager_secret" "ai_api_key" {
  count = local.ai_api_key_enabled ? 1 : 0

  name                    = "${local.name}/ai-api-key"
  description             = "Cle de l'API du modele de langage"
  recovery_window_in_days = var.secret_recovery_window_days
}

resource "aws_secretsmanager_secret_version" "ai_api_key" {
  count = local.ai_api_key_enabled ? 1 : 0

  secret_id     = aws_secretsmanager_secret.ai_api_key[0].id
  secret_string = var.ai_api_key
}
