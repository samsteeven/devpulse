# Coût AWS de la configuration par défaut

Estimation mensuelle (730 heures) du déploiement décrit dans [`infrastructure/terraform`](../../infrastructure/terraform/README.md), avec ses valeurs par défaut : région Paris (`eu-west-3`), 9 tâches Fargate x86 de 0,25 vCPU et 0,5 Go, pas de NAT, pas de Spot, HTTP seul. Prix en dollars, **hors TVA**, sans offre gratuite ni crédits.

Rien n'a été déployé : aucune facture réelle ne confirme ces chiffres.

## Sources

Prix unitaires lus le **3 novembre 2026** dans l'API de prix publique d'AWS (fichiers par service et par région, date de publication du fichier entre parenthèses) :

- `https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/<service>/current/eu-west-3/index.json` pour `AmazonECS` (Fargate, 19/10/2026), `AWSELB` (02/11/2026), `AmazonRDS` (29/10/2026), `AmazonMQ` (11/09/2026), `AmazonElastiCache` (28/10/2026), `AmazonVPC` (IPv4 publiques, 03/11/2026), `AmazonEC2` (NAT, date non relevée), `AmazonCloudWatch` (07/10/2026), `AWSSecretsManager` (11/09/2026), `AmazonECR` (11/09/2026), `AWSCloudMap` (11/09/2026), `AWSDataTransfer` (16/09/2026)
- `https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonRoute53/current/index.json` (zone hébergée, 11/09/2026)
- [Tarifs Fargate](https://aws.amazon.com/fargate/pricing/) : remise Spot « jusqu'à 70 % » ; le tableau des prix Spot par région ne s'est pas affiché à la lecture
- [Instances Amazon MQ pour RabbitMQ](https://docs.aws.amazon.com/amazon-mq/latest/developer-guide/rmq-broker-instance-types.html) et [versions](https://docs.aws.amazon.com/amazon-mq/latest/developer-guide/rabbitmq-version-management.html)

Dans les tableaux, **V** = prix unitaire vérifié dans ces sources ; **E** = quantité ou prix estimé.

## Configuration par défaut

| Poste | Calcul | $/mois | |
|---|---|---:|---|
| ALB, heures | 0,02646 $/h | 19,32 | V |
| ALB, LCU | 0,0084 $/LCU-h ; trafic de démonstration, 0,25 LCU en moyenne | 1,53 | E (quantité) |
| ALB, IPv4 publiques | 2 adresses (une par zone) × 0,005 $/h | 7,30 | V (prix), E (nombre d'adresses) |
| Fargate, vCPU | 9 × 0,25 vCPU × 0,0486 $/h | 79,83 | V |
| Fargate, mémoire | 9 × 0,5 Go × 0,0053 $/h | 17,41 | V |
| Fargate, stockage éphémère | 20 Go inclus par tâche | 0 | V |
| IPv4 publiques des tâches | 9 × 0,005 $/h (tâches en sous-réseaux publics) | 32,85 | V |
| RDS db.t4g.micro mono-zone | 0,018 $/h | 13,14 | V |
| RDS stockage gp3 | 20 Go × 0,133 $/Go-mois | 2,66 | V |
| RDS sauvegardes | gratuites jusqu'à la taille du stockage alloué (7 jours, petite base) | 0 | E |
| Amazon MQ mq.t3.micro, RabbitMQ instance unique | 0,03068 $/h | 22,40 | V |
| Amazon MQ stockage | 20 Go (taille du volume d'après la documentation, non revérifiée) × 0,116 $/Go-mois | 2,32 | V (prix), E (taille) |
| ElastiCache Valkey cache.t4g.micro | 0,0144 $/h (Redis OSS : 0,018 $/h) | 10,51 | V |
| CloudWatch Logs | 1,5 Go ingérés × 0,5985 $/Go ; stockage 0,0315 $/Go-mois | 0,95 | V (prix), E (volume) |
| Secrets Manager | 5 secrets × 0,40 $ ; appels (0,05 $/10 000) négligeables | 2,00 | V |
| ECR | 2 à 5 Go × 0,10 $/Go-mois | 0,50 | V (prix), E (volume) |
| Cloud Map et zone privée | 6 instances × 0,10 $ + zone Route 53 0,50 $ | 1,10 | V (prix), E (facturation de la zone créée par Cloud Map) |
| Transfert sortant | 0,09 $/Go au-delà de 100 Go gratuits par mois ; démonstration bien en dessous | 0 | V (prix), E (volume) |
| Trafic entre zones | quelques Go à 0,01 $/Go dans chaque sens | < 0,10 | E |
| **Total** | | **≈ 214** | |

Soit environ **0,29 $/h, 7 $/jour**. La moitié du total vient des tâches (Fargate et leurs IP publiques), un tiers des services managés à coût fixe (ALB, RDS, Amazon MQ, Valkey).

## Variantes

| Changement | Effet mensuel | |
|---|---:|---|
| `enable_nat_gateway = true` : 0 IP de tâche (−32,85), NAT 0,05 $/h (+36,50), son IP (+3,65), 0,05 $/Go traité (images, sondes : +0,10 environ) | **≈ +7** | V (prix), E (volume) |
| `use_spot_for_workers = true` : 2 workers sur 21,61 $ passent à 6,50 – 10,80 $ selon la remise réelle (50 à 70 %) | **≈ −11 à −15** | E (prix Spot de Paris non lu) |
| `cpu_architecture = "ARM64"` (images arm64 à construire) : 0,03888 $/vCPU-h et 0,00424 $/Go-h | **≈ −19** | V |
| `certificate_arn` : certificat ACM public gratuit, même ALB | 0 | E |
| `desired_counts` à 0 partout : plus de Fargate, plus d'IP de tâche, LCU quasi nulles | **≈ −132** (reste ≈ 82 $/mois) | V (prix) |
| Une deuxième tâche de gateway et d'alerting (haute disponibilité minimale) | ≈ +29 | V |

Le mode sans NAT n'économise donc presque rien à 9 tâches : 9 IP publiques coûtent à peu près une passerelle NAT. Il devient intéressant quand les tâches sont arrêtées (0 IP facturée, alors que la NAT coûte toujours ses 0,05 $/h), et cesse de l'être au-delà de 11 tâches environ.

## Pour une démonstration

Le coût fixe (ALB, RDS, Amazon MQ, Valkey, IP de l'ALB) court dès la création, même sans trafic : environ **2,70 $/jour** avec toutes les tâches à 0, **7 $/jour** avec les 9 tâches.

- **Détruire après la démonstration** (`terraform destroy`) : c'est le seul moyen de revenir à 0. Créer l'environnement le matin et le détruire le soir (10 heures) coûte de l'ordre de **3 $**. Compter 20 à 30 minutes de création (RDS et Amazon MQ sont les plus lents). Amazon MQ facture l'heure entamée.
- **Garder le NAT désactivé** (défaut) : pas de 0,05 $/h fixe, et aucune IP facturée quand les tâches sont à 0.
- **Workers sur Spot** (`use_spot_for_workers = true`) : une interruption retarde des sondes ou une analyse, sans perte (les messages restent dans RabbitMQ ou l'outbox).
- **`desired_counts` à 0 entre deux séances** si l'on veut garder les données : environ 2,70 $/jour au lieu de 7.
- Ne pas activer Container Insights ni allonger la rétention des journaux pour une démonstration.

Ordre de grandeur à retenir : **quelques dollars par jour de démonstration, environ 215 $ par mois si on oublie de détruire.**
