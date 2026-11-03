#!/usr/bin/env pwsh
# Crée un .env local avec des secrets aléatoires. Ne remplace jamais un .env existant sans -Force.
param([switch]$Force)

$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$target = Join-Path $root '.env'
if ((Test-Path $target) -and -not $Force) {
    Write-Host '.env existe déjà (utiliser -Force pour le remplacer, ce qui rend le volume PostgreSQL inutilisable).'
    exit 0
}

function New-Secret([int]$Bytes = 32) {
    $buffer = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes($Bytes)
    # Hexadécimal : aucun caractère à échapper dans une URL, un YAML ou une chaîne de connexion.
    [System.Convert]::ToHexString($buffer).ToLowerInvariant()
}

$rsa = [System.Security.Cryptography.RSA]::Create(2048)
$lines = @(
    'POSTGRES_USER=devpulse'
    "POSTGRES_PASSWORD=$(New-Secret)"
    'POSTGRES_PORT=5433'
    'RABBITMQ_USER=devpulse'
    "RABBITMQ_PASSWORD=$(New-Secret)"
    "REDIS_PASSWORD=$(New-Secret)"
    "JWT_PRIVATE_KEY=$([System.Convert]::ToBase64String($rsa.ExportPkcs8PrivateKey()))"
    "JWT_PUBLIC_KEY=$([System.Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo()))"
    "INTERNAL_API_KEY=$(New-Secret)"
    'ALLOW_PRIVATE_TARGETS=true'
)
[System.IO.File]::WriteAllLines($target, $lines)
Write-Host ".env créé : $target"
