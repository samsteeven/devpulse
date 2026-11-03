#!/usr/bin/env pwsh
# Lance les services .NET sur le poste, branchés sur l'infrastructure de docker-compose.yml.
#   pwsh infrastructure/scripts/dev.ps1          compile et démarre
#   pwsh infrastructure/scripts/dev.ps1 -NoBuild démarre sans recompiler
#   pwsh infrastructure/scripts/dev.ps1 -Stop    arrête
# Journaux dans .run/ (ignoré par Git). Le front se lance à part : cd apps/web/devpulse-web; npm run dev
param([switch]$Stop, [switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$runDir = Join-Path $root '.run'
$pidFile = Join-Path $runDir 'pids.txt'

function Stop-All {
    if (-not (Test-Path $pidFile)) { return }
    foreach ($id in Get-Content $pidFile) {
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $pidFile
}

Stop-All
if ($Stop) { Write-Host 'Services arrêtés.'; exit 0 }

$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) { throw '.env absent : lancer infrastructure/scripts/make-env.ps1' }
$dotenv = @{}
foreach ($line in Get-Content $envFile) {
    if ($line -match '^\s*([A-Z_0-9]+)=(.*)$') { $dotenv[$Matches[1]] = $Matches[2] }
}

if (-not $NoBuild) {
    dotnet build (Join-Path $root 'DevPulse.slnx') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'La compilation a échoué.' }
}

New-Item -ItemType Directory -Force $runDir | Out-Null

function Connection([string]$database) {
    "Host=localhost;Port=$($dotenv.POSTGRES_PORT);Database=$database;Username=$($dotenv.POSTGRES_USER);Password=$($dotenv.POSTGRES_PASSWORD)"
}

$common = @{
    ASPNETCORE_ENVIRONMENT      = 'Development'
    DOTNET_ENVIRONMENT          = 'Development'
    Jwt__PublicKey              = $dotenv.JWT_PUBLIC_KEY
    OTEL_EXPORTER_OTLP_ENDPOINT = 'http://localhost:4317'
}

$services = @(
    @{ Name = 'identity';   Project = 'src/Services/Identity/DevPulse.Identity.Api';     Port = 5010; Env = @{ ConnectionStrings__Database = (Connection 'devpulse_identity'); Jwt__PrivateKey = $dotenv.JWT_PRIVATE_KEY; Database__MigrateOnStartup = 'true' } }
    @{ Name = 'registry';   Project = 'src/Services/Registry/DevPulse.Registry.Api';     Port = 5011; Env = @{ ConnectionStrings__Database = (Connection 'devpulse_registry'); InternalApi__Key = $dotenv.INTERNAL_API_KEY; TargetPolicy__AllowPrivateTargets = $dotenv.ALLOW_PRIVATE_TARGETS; Database__MigrateOnStartup = 'true' } }
    @{ Name = 'monitoring'; Project = 'src/Services/Monitoring/DevPulse.Monitoring.Api'; Port = 5012; Env = @{ ConnectionStrings__Database = (Connection 'devpulse_monitoring'); Database__MigrateOnStartup = 'true' } }
    @{ Name = 'gateway';    Project = 'src/Gateway/DevPulse.Gateway';                    Port = 8080; Env = @{} }
    @{ Name = 'demo';       Project = 'infrastructure/demo/DevPulse.DemoTarget';         Port = 5020; Env = @{} }
    @{ Name = 'worker';     Project = 'src/Services/Monitoring/DevPulse.Monitoring.Worker'; Port = 0; Env = @{ ConnectionStrings__Database = (Connection 'devpulse_monitoring'); InternalApi__Key = $dotenv.INTERNAL_API_KEY; TargetPolicy__AllowPrivateTargets = $dotenv.ALLOW_PRIVATE_TARGETS; Registry__BaseUrl = 'http://localhost:5011/'; Monitoring__SyncIntervalSeconds = '5' } }
)

foreach ($service in $services) {
    $dir = Join-Path $root $service.Project
    $assembly = Split-Path $service.Project -Leaf
    $dll = Join-Path $dir "bin/Debug/net10.0/$assembly.dll"

    foreach ($pair in ($common.GetEnumerator() + $service.Env.GetEnumerator())) { Set-Item "env:$($pair.Key)" $pair.Value }
    $env:ASPNETCORE_URLS = if ($service.Port) { "http://localhost:$($service.Port)" } else { '' }

    # Le dossier de travail est celui du projet : c'est là que se trouve appsettings.json.
    $process = Start-Process dotnet -ArgumentList "`"$dll`"" -WorkingDirectory $dir -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $runDir "$($service.Name).log") `
        -RedirectStandardError (Join-Path $runDir "$($service.Name).err")
    Add-Content $pidFile $process.Id

    foreach ($pair in $service.Env.GetEnumerator()) { Remove-Item "env:$($pair.Key)" }

    # Les API appliquent leurs migrations au démarrage : le worker, lancé en dernier, trouve ses tables.
    if ($service.Name -in 'identity', 'registry', 'monitoring', 'gateway') {
        $path = if ($service.Name -eq 'gateway') { '/health/live' } else { '/health/ready' }
        $deadline = (Get-Date).AddSeconds(60)
        while ($true) {
            try {
                if ((Invoke-WebRequest "http://localhost:$($service.Port)$path" -TimeoutSec 2 -SkipHttpErrorCheck).StatusCode -eq 200) { break }
            } catch { }
            if ($process.HasExited) { throw "$($service.Name) s'est arrêté au démarrage : voir .run/$($service.Name).err et .log" }
            if ((Get-Date) -gt $deadline) { throw "$($service.Name) n'est pas prêt après 60 s : voir .run/$($service.Name).log" }
            Start-Sleep -Milliseconds 300
        }
    }
    Write-Host "$($service.Name) démarré"
}

Write-Host "`nPasserelle : http://localhost:8080  ·  Cible de démonstration : http://localhost:5020/health"
