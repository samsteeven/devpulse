# Coût Azure de la configuration par défaut

Estimation mensuelle (730 heures) du déploiement décrit dans [`infrastructure/terraform-azure`](../../infrastructure/terraform-azure/README.md), avec ses valeurs par défaut : région **France Central** (`francecentral`), réseau virtuel activé, pas de Redis, RabbitMQ en conteneur, applications à 0,25 vCPU et 0,5 Gio. Prix en dollars, hors taxes, sans crédit déduit. Un abonnement Azure for Students doit utiliser une de ses régions autorisées (voir le README) : les prix varient d'une région à l'autre, refaire le calcul pour la sienne.

Rien n'a été déployé : aucune facture réelle ne confirme ces chiffres.

## Sources

Prix unitaires lus le **3 novembre 2026** dans l'API publique [Azure Retail Prices](https://prices.azure.com/api/retail/prices), filtre `priceType eq 'Consumption'`, région `francecentral` sauf mention :

- `serviceName eq 'Azure Container Apps'` : vCPU actif 0,000024 $/s, vCPU inactif 0,000003 $/s, mémoire 0,000003 $/Gio-s (actif comme inactif), requêtes 0,40 $/million
- `serviceName eq 'Azure Database for PostgreSQL'` : B1MS 0,019 $/h, stockage 0,133 $/Go-mois, sauvegarde 0,10 $/Go-mois
- `serviceName eq 'Load Balancer'` (région `Global`) : Standard, 5 premières règles 0,025 $/h, données traitées 0,005 $/Go
- `serviceName eq 'Virtual Network'` : IPv4 publique statique Standard 0,005 $/h
- `serviceName eq 'Azure DNS'` (zone globale) : zone privée 0,50 $ (25 premières), requêtes 0,40 $/million
- `serviceName eq 'Log Analytics'` : ingestion 0 $ jusqu'à 5 Go, puis 2,76 $/Go ; rétention au-delà de l'inclus 0,12 $/Go-mois
- `serviceName eq 'Bandwidth'` : sortie Internet 0 $ jusqu'à 100 Go, puis 0,08 à 0,087 $/Go
- `serviceName eq 'Redis Cache'` : Azure Managed Redis Balanced B0, 0,018 $/h par instance

Et, lus le même jour :

- [Facturation de Container Apps](https://learn.microsoft.com/azure/container-apps/billing) : gratuits chaque mois civil et par abonnement, 180 000 vCPU-s, 360 000 Gio-s et 2 millions de requêtes ; tarif inactif seulement pour un réplica d'une révision à son nombre minimum (> 0), sans requête en cours, sous 0,01 vCPU et 1 000 octets/s de trafic ; un réplica au-delà du minimum est toujours au tarif actif
- [Réseaux virtuels de Container Apps](https://learn.microsoft.com/azure/container-apps/custom-virtual-networks) : avec son propre réseau, un environnement externe fait facturer un répartiteur standard et deux IP publiques
- [Azure for Students](https://azure.microsoft.com/free/students/) : 750 heures de Flexible Server Burstable B1MS, 32 Go de stockage et 32 Go de sauvegarde par mois pendant 12 mois, « new Azure customers only »
- [Arrêt d'un serveur flexible](https://learn.microsoft.com/azure/postgresql/configure-maintain/how-to-stop-server) : redémarrage automatique après 7 jours

Dans les tableaux, **V** = prix unitaire vérifié dans ces sources ; **E** = quantité, comportement ou prix estimé.

## Configuration par défaut

Six réplicas tournent en permanence : incident, alerting, monitoring-worker, ai-worker, rabbitmq et registry (que monitoring-worker appelle toutes les 30 s). Chacun représente 657 000 vCPU-s et 1 314 000 Gio-s par mois, soit **19,71 $ au tarif actif, 5,91 $ au tarif inactif**. Le tarif réel dépend de l'activité de chaque réplica, que personne n'a mesurée : l'estimation prend rabbitmq et monitoring-worker actifs (statistiques du plugin de gestion, sondes), les quatre autres inactifs la plupart du temps.

| Poste | Calcul | $/mois | |
|---|---|---:|---|
| Container Apps, vCPU des 6 réplicas permanents | 2 actifs (1 314 000 vCPU-s, dont 180 000 gratuits) × 0,000024 + 4 inactifs (2 628 000) × 0,000003 | 35,10 | V (prix), E (actif ou inactif) |
| Container Apps, mémoire des 6 réplicas permanents | (7 884 000 − 360 000 gratuits) Gio-s × 0,000003 | 22,57 | V |
| Container Apps, applications à 0 (web, gateway, identity, monitoring-api) | 1 h d'usage par jour plus 5 min de délai d'arrêt, 4 applications : environ 132 réplica-heures au tarif actif | 3,56 | E (usage) |
| Container Apps, requêtes | moins de 2 millions par mois, gratuites ; sondes non facturées | 0 | V (prix), E (volume) |
| PostgreSQL B1ms | 0,019 $/h | 13,87 | V |
| PostgreSQL, stockage | 32 Go × 0,133 $ | 4,26 | V |
| PostgreSQL, sauvegardes | 7 jours d'une petite base, sous la taille du stockage alloué, supposées incluses | 0 | E |
| Répartiteur standard de l'environnement | 0,025 $/h ; données traitées négligeables | 18,25 | V (prix), E (compteur appliqué) |
| IP publiques de l'environnement | 2 × 0,005 $/h | 7,30 | V |
| Réseau virtuel, sous-réseaux, lien DNS | gratuits | 0 | E |
| Zone DNS privée | 1 zone ; quelques milliers de requêtes | 0,50 | V |
| Log Analytics | plafond 0,15 Go/jour, au plus 4,65 Go/mois, sous les 5 Go gratuits ; 30 jours de rétention inclus | 0 | V (prix), E (volume, portée des 5 Go, rétention incluse) |
| Transfert sortant | sous les 100 Go gratuits | 0 | V (prix), E (volume) |
| **Total** | | **≈ 105** | |

Soit environ **0,14 $/h, 3,50 $/jour**. Bornes : 82 $ si les six réplicas permanents restent inactifs, 161 $ s'ils sont tous actifs.

**Offre gratuite PostgreSQL.** La page Azure for Students liste elle-même les 750 heures de B1MS, 32 Go de stockage et 32 Go de sauvegarde pendant 12 mois : l'offre vaut donc pour cet abonnement, mais seulement pour un **nouveau client Azure**. Elle retirerait 18,13 $, **total ≈ 87 $/mois**. Non vérifié : qu'un serveur créé par Terraform (et non par l'option « offre gratuite » du portail) reçoive bien ce compteur gratuit ; le confirmer dans Cost Management après un jour.

**Durée de 100 $ de crédit** en marche continue : environ **4 semaines** (105 $/mois), **5 semaines** avec l'offre PostgreSQL (87 $/mois). Le budget par défaut (20 $) déclenche donc son alerte à 50 % en trois jours environ : c'est voulu.

## Variantes

| Changement | Effet mensuel | |
|---|---:|---|
| `enable_vnet = false` : plus de répartiteur, d'IP publiques ni de zone DNS ; PostgreSQL public (règle « services Azure ») | **≈ −26** | V (prix), E (absence de frais sans réseau propre) |
| `external_rabbitmq_host` (CloudAMQP gratuit) : un réplica actif de moins | ≈ −20 | E |
| `registry` à min 0 (réveillé toutes les 30 s, donc toujours au tarif actif) | ≈ +14 | E |
| `enable_redis = true` : Balanced B0 sans haute disponibilité (avec : deux instances supposées) | +13,14 (≈ +26) | V (prix), E (facturation HA) |
| `daily_quota_gb = 0.5` : jusqu'à 15,5 Go/mois, 10,5 Go facturés au pire | jusqu'à +29 | V |
| PostgreSQL arrêté (calcul seulement, stockage toujours facturé) | −13,87 au prorata, 7 jours au plus | V |
| web et gateway à min 1 (plus de démarrage à froid) | ≈ +12 (inactifs) à +39 (actifs) | E |

## Pour une démonstration

Le coût fixe (PostgreSQL, répartiteur et IP de l'environnement, RabbitMQ et les consommateurs toujours démarrés) court dès la création, même sans visiteur.

- **Détruire après la démonstration** (`terraform destroy`) : c'est le seul moyen de revenir à 0. Une séance de 10 heures reste dans l'offre gratuite mensuelle de Container Apps (environ 90 000 vCPU-s pour une dizaine de réplicas) si elle n'a pas été consommée ; reste PostgreSQL et le réseau (0,60 $), plus la zone DNS si elle est facturée au mois entamé (0,50 $) : **environ 1 $** (E). Durée de création non mesurée.
- **Arrêter PostgreSQL** entre deux séances proches (`az postgres flexible-server stop`) : il redémarre seul au bout de 7 jours, et le stockage reste facturé.
- **Garder les réplicas minimum par défaut** : web, gateway, identity et monitoring-api à 0. Les monter à 1 supprime le démarrage à froid mais coûte de 6 à 20 $ par application et par mois.
- **`enable_vnet = false`** pour une démonstration courte, si l'exposition de PostgreSQL aux adresses Azure est acceptée : c'est le quart de la facture.
- Ne pas relever `daily_quota_gb` ni la rétention des journaux.

Ordre de grandeur à retenir : **environ 1 $ par séance de démonstration détruite le soir, environ 105 $ par mois si on oublie de détruire, soit tout le crédit étudiant en un mois.**
