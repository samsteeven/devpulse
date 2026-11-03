# Secrets générés par Terraform et passés aux applications comme secrets Container Apps (pas de Key Vault, pour
# rester au plus bas coût). Ils sont aussi dans l'état Terraform, en clair : l'état doit être chiffré et d'accès
# restreint (exemple de backend dans versions.tf). Étape suivante en production : Key Vault, et une identité
# managée par application qui ne lit que ses secrets (`secret.key_vault_secret_id` + `identity`).

# Caractères alphanumériques seulement : ces valeurs entrent dans des chaînes de connexion où « ; », « = », « , »,
# « @ » ou « / » auraient un sens.
# Azure impose au mot de passe PostgreSQL trois catégories sur quatre : majuscules, minuscules et chiffres garantis.
resource "random_password" "db" {
  length      = 32
  special     = false
  min_upper   = 2
  min_lower   = 2
  min_numeric = 2
}

resource "random_password" "mq" {
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
  # Même dérivation que infrastructure/terraform/secrets.tf.
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

# --- Valeurs des secrets Container Apps ---

locals {
  # `nonsensitive` porte sur le booléen « valeur fournie ou non », pas sur la valeur.
  ai_api_key_enabled = nonsensitive(var.ai_api_key != "")
  registry_auth      = nonsensitive(var.registry_password != "")

  # Nom du secret Container Apps (minuscules, chiffres, « - ») => valeur. Chaque application ne reçoit que les
  # secrets que ses variables référencent (apps.tf). Les secrets facultatifs sont filtrés par `if` : deux objets
  # de formes différentes ne peuvent pas être les deux branches d'une condition.
  optional_secrets = {
    "redis-connection"  = { enabled = var.enable_redis, value = local.redis_connection_string }
    "ai-api-key"        = { enabled = local.ai_api_key_enabled, value = var.ai_api_key }
    "registry-password" = { enabled = local.registry_auth, value = var.registry_password }
    # Contient la clé d'instrumentation : quiconque la lit peut écrire dans Application Insights.
    "appinsights-connection" = { enabled = var.enable_application_insights, value = local.app_insights_connection_string }
  }

  secret_values = merge(
    { for key, value in local.connection_strings : "db-${key}" => value },
    {
      "jwt-private-key"   = local.jwt_private_key_b64
      "internal-api-key"  = random_password.internal_api_key.result
      "rabbitmq-password" = local.rabbitmq_password
    },
    { for name, secret in local.optional_secrets : name => secret.value if secret.enabled },
  )
}
