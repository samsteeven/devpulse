# VPC sur deux zones : sous-réseaux publics (ALB, et tâches si pas de NAT) et privés (RDS, Amazon MQ, ElastiCache,
# et tâches si NAT).
#
# Sans NAT, les tâches ont une IP publique pour joindre ECR, Secrets Manager et Internet (sondes, webhooks, LLM).
# Elles restent fermées en entrée : leurs groupes de sécurité n'acceptent que l'ALB et les autres tâches.
# Coût : 0,005 $/h par IP publique (9 tâches : environ 33 $/mois), contre 0,05 $/h plus 0,05 $/Go traité pour une
# passerelle NAT (environ 40 $/mois avec son IP). L'écart est faible à 9 tâches et s'inverse vers 11. L'intérêt réel
# du mode sans NAT : il ne coûte plus rien quand les services sont à 0 tâche.
# Le prix à payer : une erreur de groupe de sécurité exposerait directement une tâche, ce qu'un sous-réseau privé
# empêche.

terraform {
  required_providers {
    aws = {
      source = "hashicorp/aws"
    }
  }
}

data "aws_availability_zones" "available" {
  state = "available"
}

locals {
  azs = slice(data.aws_availability_zones.available.names, 0, 2)
}

resource "aws_vpc" "this" {
  cidr_block = var.cidr

  # Nécessaires à la zone DNS privée Cloud Map (devpulse.local).
  enable_dns_support   = true
  enable_dns_hostnames = true

  tags = {
    Name = var.name
  }
}

resource "aws_internet_gateway" "this" {
  vpc_id = aws_vpc.this.id

  tags = {
    Name = var.name
  }
}

resource "aws_subnet" "public" {
  count = 2

  vpc_id            = aws_vpc.this.id
  cidr_block        = cidrsubnet(var.cidr, 8, count.index)
  availability_zone = local.azs[count.index]

  # Les tâches reçoivent leur IP publique par `assign_public_ip`, pas par défaut au niveau du sous-réseau.
  map_public_ip_on_launch = false

  tags = {
    Name = "${var.name}-public-${local.azs[count.index]}"
    Tier = "public"
  }
}

resource "aws_subnet" "private" {
  count = 2

  vpc_id            = aws_vpc.this.id
  cidr_block        = cidrsubnet(var.cidr, 8, 10 + count.index)
  availability_zone = local.azs[count.index]

  tags = {
    Name = "${var.name}-private-${local.azs[count.index]}"
    Tier = "private"
  }
}

resource "aws_route_table" "public" {
  vpc_id = aws_vpc.this.id

  tags = {
    Name = "${var.name}-public"
  }
}

resource "aws_route" "public_internet" {
  route_table_id         = aws_route_table.public.id
  destination_cidr_block = "0.0.0.0/0"
  gateway_id             = aws_internet_gateway.this.id
}

resource "aws_route_table_association" "public" {
  count = 2

  subnet_id      = aws_subnet.public[count.index].id
  route_table_id = aws_route_table.public.id
}

# Une seule passerelle NAT, dans la première zone : si cette zone tombe, les tâches de l'autre perdent Internet.
# Une NAT par zone doublerait ce poste, sans intérêt pour une démonstration.
resource "aws_eip" "nat" {
  count = var.enable_nat_gateway ? 1 : 0

  domain = "vpc"

  tags = {
    Name = "${var.name}-nat"
  }
}

resource "aws_nat_gateway" "this" {
  count = var.enable_nat_gateway ? 1 : 0

  allocation_id = aws_eip.nat[0].id
  subnet_id     = aws_subnet.public[0].id

  tags = {
    Name = var.name
  }

  depends_on = [aws_internet_gateway.this]
}

# Sans NAT, la table privée n'a pas de route vers Internet : RDS, Amazon MQ et ElastiCache n'en ont pas besoin.
resource "aws_route_table" "private" {
  vpc_id = aws_vpc.this.id

  tags = {
    Name = "${var.name}-private"
  }
}

resource "aws_route" "private_nat" {
  count = var.enable_nat_gateway ? 1 : 0

  route_table_id         = aws_route_table.private.id
  destination_cidr_block = "0.0.0.0/0"
  nat_gateway_id         = aws_nat_gateway.this[0].id
}

resource "aws_route_table_association" "private" {
  count = 2

  subnet_id      = aws_subnet.private[count.index].id
  route_table_id = aws_route_table.private.id
}
