-- Exécuté une seule fois, à la création d'un volume vide.
-- Une instance en local, mais une base par service : aucune clé étrangère ne traverse ces bases.
CREATE DATABASE devpulse_identity;
CREATE DATABASE devpulse_registry;
CREATE DATABASE devpulse_monitoring;
CREATE DATABASE devpulse_incident;
CREATE DATABASE devpulse_alerting;
