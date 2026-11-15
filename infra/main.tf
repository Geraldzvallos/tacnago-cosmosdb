terraform {
  required_version = ">= 1.6"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }

  # El estado se guarda en una Storage Account de Azure (la crea infra.yml si no existe).
  # Los datos se pasan en "terraform init" con -backend-config.
  backend "azurerm" {}
}

# ------------------------------------------------------------------
# Variables
# ------------------------------------------------------------------
variable "subscription_id" {
  type        = string
  description = "ID de la suscripción de Azure"
}

variable "location" {
  type        = string
  description = "Región de Azure (westus3 está permitida en Azure for Students)"
  default     = "westus3"
}

variable "cosmos_account_name" {
  type        = string
  description = "Cuenta de Cosmos DB con capa gratuita que ya existe en la suscripción"
  default     = "qa-bitacora-cosmos-gzp"
}

variable "cosmos_resource_group" {
  type        = string
  description = "Grupo de recursos de esa cuenta"
  default     = "rg-qa-bitacora"
}

variable "suffix" {
  type        = string
  description = "Sufijo para que los nombres sean únicos"
  default     = "geraldzvallos"
}

variable "container_image" {
  type        = string
  description = "Imagen inicial del contenedor en GHCR (la actualiza deployimage.yml)"
  default     = "geraldzvallos/tacnago-api:latest"
}

variable "ghcr_username" {
  type        = string
  description = "Usuario de GitHub para descargar la imagen de GHCR"
}

variable "ghcr_password" {
  type        = string
  description = "Token de GitHub (read:packages) para descargar la imagen de GHCR"
  sensitive   = true
}

provider "azurerm" {
  features {}
  subscription_id = var.subscription_id
}

resource "azurerm_resource_group" "rg" {
  name     = "rg-tacnago-${var.suffix}"
  location = var.location
}

# ------------------------------------------------------------------
# Base de datos NoSQL: Azure Cosmos DB for NoSQL (API nativa, no la API de MongoDB)
# ------------------------------------------------------------------
# Azure permite UNA sola cuenta de Cosmos DB con capa gratuita por suscripción
# (1000 RU/s y 25 GB gratis para toda la cuenta). La suscripción ya tenía esa cuenta,
# así que TacnaGo crea su propia base de datos dentro de ella en lugar de una cuenta nueva.
data "azurerm_cosmosdb_account" "cosmos" {
  name                = var.cosmos_account_name
  resource_group_name = var.cosmos_resource_group
}

resource "azurerm_cosmosdb_sql_database" "db" {
  name                = "TacnaGo"
  resource_group_name = data.azurerm_cosmosdb_account.cosmos.resource_group_name
  account_name        = data.azurerm_cosmosdb_account.cosmos.name
  throughput          = 400 # RU/s compartidos; sumados a los de la otra base siguen dentro de los 1000 RU/s gratis
}

resource "azurerm_cosmosdb_sql_container" "lugares" {
  name                  = "Lugares"
  resource_group_name   = data.azurerm_cosmosdb_account.cosmos.resource_group_name
  account_name          = data.azurerm_cosmosdb_account.cosmos.name
  database_name         = azurerm_cosmosdb_sql_database.db.name
  partition_key_paths   = ["/categoria"] # clave de partición: un lugar vive en la partición de su categoría
  partition_key_version = 2

  indexing_policy {
    indexing_mode = "consistent"

    included_path {
      path = "/*"
    }

    # Nunca se filtra por la descripción: no indexarla ahorra RU en cada escritura
    excluded_path {
      path = "/descripcion/?"
    }
  }
}

# Contenedor de prueba: el workflow restore.yml restaura aquí el último respaldo cada semana
# para comprobar que los respaldos sirven (usa los RU/s compartidos de la base, sin costo extra).
resource "azurerm_cosmosdb_sql_container" "lugares_restore" {
  name                  = "Lugares-restore"
  resource_group_name   = data.azurerm_cosmosdb_account.cosmos.resource_group_name
  account_name          = data.azurerm_cosmosdb_account.cosmos.name
  database_name         = azurerm_cosmosdb_sql_database.db.name
  partition_key_paths   = ["/categoria"]
  partition_key_version = 2
}

# ------------------------------------------------------------------
# Respaldos lógicos: Azure Blob Storage (cuenta separada de la base de datos)
# ------------------------------------------------------------------
resource "azurerm_storage_account" "backups" {
  name                            = "sttacnagobk${substr(var.suffix, 0, 6)}"
  resource_group_name             = azurerm_resource_group.rg.name
  location                        = azurerm_resource_group.rg.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false

  blob_properties {
    versioning_enabled = true # cada sobrescritura guarda la versión anterior

    delete_retention_policy {
      days = 7 # un respaldo borrado por error se puede recuperar durante 7 días
    }

    container_delete_retention_policy {
      days = 7
    }
  }
}

resource "azurerm_storage_container" "backups" {
  name                  = "cosmos-backups"
  storage_account_id    = azurerm_storage_account.backups.id
  container_access_type = "private"
}

# Política de retención: Hot los primeros 7 días, luego Cool (más barato) y se borra a los 37 días
resource "azurerm_storage_management_policy" "retencion" {
  storage_account_id = azurerm_storage_account.backups.id

  rule {
    name    = "retencion-respaldos"
    enabled = true

    filters {
      prefix_match = ["cosmos-backups/tacnago/"]
      blob_types   = ["blockBlob"]
    }

    actions {
      base_blob {
        tier_to_cool_after_days_since_modification_greater_than = 7
        delete_after_days_since_modification_greater_than       = 37
      }

      version {
        delete_after_days_since_creation = 37
      }
    }
  }
}

# ------------------------------------------------------------------
# Backend: Azure App Service (Linux, plan F1 gratuito) con contenedor
# ------------------------------------------------------------------
resource "azurerm_service_plan" "plan" {
  name                = "asp-tacnago-${var.suffix}"
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  os_type             = "Linux"
  sku_name            = "F1"
}

resource "azurerm_linux_web_app" "app" {
  name                = "app-tacnago-${var.suffix}"
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  service_plan_id     = azurerm_service_plan.plan.id
  https_only          = true

  site_config {
    always_on           = false # el plan F1 no permite always_on
    minimum_tls_version = "1.2"
    application_stack {
      docker_image_name        = var.container_image
      docker_registry_url      = "https://ghcr.io"
      docker_registry_username = var.ghcr_username
      docker_registry_password = var.ghcr_password
    }
  }

  # ASP.NET Core lee Cosmos__Endpoint como la clave Cosmos:Endpoint de la configuración.
  # La clave de Cosmos DB va de Terraform a la Web App: no está en el código ni en GitHub.
  app_settings = {
    "WEBSITES_PORT"            = "8080"
    "Cosmos__Endpoint"         = data.azurerm_cosmosdb_account.cosmos.endpoint
    "Cosmos__Key"              = data.azurerm_cosmosdb_account.cosmos.primary_key
    "Cosmos__Database"         = azurerm_cosmosdb_sql_database.db.name
    "Cosmos__Container"        = azurerm_cosmosdb_sql_container.lugares.name
    "Backup__ConnectionString" = azurerm_storage_account.backups.primary_connection_string
    "Backup__Container"        = azurerm_storage_container.backups.name
  }

  # La imagen la cambia deployimage.yml en cada despliegue; Terraform no debe revertirla.
  lifecycle {
    ignore_changes = [site_config[0].application_stack[0].docker_image_name]
  }
}

# ------------------------------------------------------------------
# Salidas
# ------------------------------------------------------------------
output "cosmos_account" {
  value = data.azurerm_cosmosdb_account.cosmos.name
}

output "cosmos_endpoint" {
  value = data.azurerm_cosmosdb_account.cosmos.endpoint
}

output "webapp_name" {
  value = azurerm_linux_web_app.app.name
}

output "webapp_url" {
  value = "https://${azurerm_linux_web_app.app.default_hostname}"
}

output "backup_storage_account" {
  value = azurerm_storage_account.backups.name
}
