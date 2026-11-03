variable "name" {
  description = "Préfixe des noms de ressources."
  type        = string
}

variable "cidr" {
  description = "Plage du VPC (au moins /16 : les sous-réseaux sont des /24)."
  type        = string
}

variable "enable_nat_gateway" {
  description = "Crée une passerelle NAT et une route par défaut pour les sous-réseaux privés."
  type        = bool
}
