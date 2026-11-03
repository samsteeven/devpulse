# Déploiement AWS : ECS Fargate avec Terraform

Mise en œuvre de l'[ADR-008](../../docs/adr/README.md) : la plateforme complète sur ECS Fargate, région Paris (`eu-west-3`) par défaut.

**Ce code n'a jamais été appliqué.** Personne n'a lancé `terraform plan`, encore moins `apply`. La CI exécute `fmt -check`, `init -backend=false` et `validate`, qui passent : cela vérifie la syntaxe, les types et les références, pas le comportement chez AWS.

## Ce qui est vérifié, et ce qui ne l'est pas

| Élément | Vérifié | Pas vérifié |
|---|---|---|
| Noms des variables d'environnement | Relus dans le code des services (`RabbitMqOptions`, `AiOptions`, `JwtOptions`, configuration YARP et `ForwardedHeaders` de la passerelle) | Que chaque service démarre avec exactement cette configuration |
| Format des clés JWT | La dérivation PEM vers base64 du DER (expression régulière de `secrets.tf`) a été rejouée en .NET sur des PEM PKCS#8 et SPKI : résultat identique à `ExportPkcs8PrivateKey` / `ExportSubjectPublicKeyInfo`. Un bloc `check` le contrôle à chaque plan | Sur une clé produite par le fournisseur `tls` lui-même |
| Arguments des ressources | `terraform validate` en CI ; relus dans la documentation du fournisseur `hashicorp/aws` pour ElastiCache, Amazon MQ, ECS, RDS, ALB | Un vrai `plan` : des valeurs refusées seulement par l'API AWS (version de Valkey, jeton d'authentification) restent possibles |
| Version RabbitMQ | Documentation Amazon MQ : 3.13 sur mq.t3, 4.2 seulement sur mq.m7g | Création réelle du broker |
| Coût | Prix unitaires lus dans l'API de prix AWS : [`docs/architecture/cout-aws.md`](../../docs/architecture/cout-aws.md) | Une facture réelle |
| Tout le reste (réseau, IAM, déploiement, WebSocket à travers l'ALB) | Rien | Tout |

## Ce qui est créé

```
 Internet ─► ALB (80, 443 si certificat) ─┬─ /api/*, /hubs/* ─► gateway ──► identity, registry, monitoring-api,
                                          │                     (YARP)     incident, alerting   (*.devpulse.local:8080)
                                          └─ reste ───────────► web (nginx, SPA)
 monitoring-worker ─► registry ; sondes vers Internet          ai-worker ─► RabbitMQ (et le LLM si Provider = Llm)
 API et workers ─► RDS PostgreSQL 17, Amazon MQ (RabbitMQ 3.13, AMQPS 5671) ; alerting ─► Valkey (TLS + jeton)
```

| Fichier | Contenu |
|---|---|
| `modules/network` | VPC sur 2 zones, sous-réseaux publics et privés, NAT facultative |
| `modules/data` | RDS PostgreSQL (db.t4g.micro, 20 Go gp3 chiffrés), Amazon MQ (mq.t3.micro, instance unique), ElastiCache Valkey (cache.t4g.micro), leurs groupes de sécurité |
| `modules/ecs-service` | Un service : journaux, rôle d'exécution, définition de tâche, Cloud Map, rattachement à l'ALB |
| `main.tf` | Les 9 conteneurs et leur rôle réseau, dépôts ECR (scan à l'envoi, 10 dernières images) |
| `security.tf` | Groupes de sécurité des tâches et de l'ALB |
| `secrets.tf` | Mots de passe aléatoires, paire de clés JWT, Secrets Manager |
| `alb.tf`, `ecs.tf` | Répartiteur et règles ; cluster, zone `devpulse.local`, variables de chaque conteneur |

## Choix et simplifications

- **NAT ou pas** (`enable_nat_gateway`, faux par défaut). Sans NAT, les tâches tournent en sous-réseaux publics avec une IP publique, fermées en entrée par leurs groupes de sécurité (aucune règle depuis Internet). Avec NAT, elles passent en sous-réseaux privés derrière une seule passerelle. À 9 tâches l'écart de coût est faible (environ 33 $/mois d'IP publiques contre environ 40 $/mois de NAT) ; le mode sans NAT ne coûte plus rien quand les services sont à 0 tâche. Le risque du mode sans NAT : une erreur de groupe de sécurité exposerait une tâche directement.
- **Sans certificat, HTTP seul.** Jetons et mots de passe circulent en clair : démonstration courte uniquement. Dans ce cas, `Auth__SecureCookie` passe à `false` pour Identity, sinon le navigateur ne renverrait pas le cookie de rafraîchissement sur `http://`. Avec `certificate_arn`, HTTP redirige vers HTTPS et le cookie reste `Secure`.
- **Une instance RDS, un utilisateur maître.** Chaque service crée sa base à sa première migration (`Database__MigrateOnStartup=true`). Étape suivante : un utilisateur PostgreSQL par service, limité à sa base. `SSL Mode=Require` chiffre sans vérifier le certificat ; `VerifyFull` demanderait le bundle CA de RDS dans les images.
- **Secrets.** Les valeurs sensibles passent par `secrets` (lus par ECS au démarrage), le reste par `environment`. Chaque service a son propre rôle d'exécution, qui ne lit que ses secrets et ne tire que son image. Le rôle des tâches n'a aucune permission. Les chaînes de connexion sont des clés d'un seul secret JSON (`ARN:clé::`), pour payer un secret au lieu de six.
- **Clés et mots de passe dans l'état Terraform.** Ils sont générés par Terraform, donc présents en clair dans l'état : il doit être chiffré et d'accès restreint. Un exemple de backend S3 avec KMS est en commentaire dans `versions.tf`. L'alternative (secrets créés hors Terraform, référencés par ARN) retire ce risque mais ajoute une étape manuelle.
- **Pas de haute disponibilité.** RDS mono-zone, broker en instance unique, un nœud Valkey, une tâche par service. Une maintenance du broker interrompt les échanges ; l'outbox des services garde les messages en base.
- **Pas de `readonlyRootFilesystem`.** ASP.NET Core écrit dans `/tmp`, et Fargate ne monte pas de tmpfs.
- **Pas de contrôle de santé dans les conteneurs.** Seuls gateway (`/health/live`) et web (`/`) sont contrôlés, par l'ALB. Les API internes ne sont relancées que si leur processus s'arrête : les images (aspnet « chiseled ») n'ont ni shell ni `curl`.
- **monitoring-worker ne migre pas.** S'il démarre avant que monitoring-api ait créé le schéma, il échoue et ECS le relance.
- **Pas de points de terminaison VPC.** ECR, Secrets Manager et CloudWatch sont joints par Internet (IP publique ou NAT).
- **Tags ECR immuables.** `image_tag` doit être unique par version (SHA du commit) ; pousser deux fois `latest` échoue.
- `TargetPolicy__AllowPrivateTargets=false` est posé explicitement sur registry, monitoring-api, monitoring-worker et alerting.

## Déployer

Prérequis : Terraform 1.9 ou plus, AWS CLI authentifiée, Docker, les images des services (leurs Dockerfile sont hors de ce dossier).

```bash
cd infrastructure/terraform
cp terraform.tfvars.example terraform.tfvars      # puis renseigner image_tag
terraform init
terraform apply -target=aws_ecr_repository.this   # les dépôts d'abord : les services ne démarrent pas sans image
```

Pousser chaque image au tag `image_tag` (adresses : `terraform output ecr_repository_urls`) :

```bash
aws ecr get-login-password --region eu-west-3 | docker login --username AWS --password-stdin <compte>.dkr.ecr.eu-west-3.amazonaws.com
docker push <url-du-dépôt>:<image_tag>
```

Puis :

```bash
terraform plan        # à relire : premier plan jamais lancé
terraform apply
terraform output app_url
```

Passer un worker de Fargate à Fargate Spot (`use_spot_for_workers`) change la stratégie de capacité d'un service existant, ce qu'ECS n'accepte qu'avec un nouveau déploiement forcé : remplacer le service (`terraform apply -replace='module.service["ai-worker"].aws_ecs_service.this'`).

## Arrêter

- `desired_counts` à 0 pour tous les services : plus de calcul ni d'IP publique de tâche, mais ALB, RDS, Amazon MQ et Valkey restent facturés (environ 2,70 $/jour).
- `terraform destroy` : tout disparaît, données comprises (pas d'instantané final par défaut, secrets supprimés sans délai, dépôts ECR vidés).

Coût détaillé : [`docs/architecture/cout-aws.md`](../../docs/architecture/cout-aws.md).
