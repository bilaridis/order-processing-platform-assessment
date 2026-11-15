# Test. Same modules and structure as production (ADR-0011); smaller and cheaper values only.
# Applied by the pipeline on every merge to main.

terraform {
  required_version = ">= 1.9"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.79"
    }
  }

  # State per environment in its own storage account: locking by blob lease, versioning on, access
  # limited to the pipeline identity. Created once, outside this configuration (see the README).
  backend "azurerm" {
    resource_group_name  = "rg-orders-tfstate"
    storage_account_name = "storderstesttfstate"
    container_name       = "tfstate"
    key                  = "test.tfstate"
    use_azuread_auth     = true # No storage key: the pipeline's identity reads and locks the state.
  }
}

provider "azurerm" {
  features {}
  storage_use_azuread = true
  # subscription_id comes from ARM_SUBSCRIPTION_ID, set by the pipeline (OIDC login).
}

locals {
  environment = "test"
  tags        = { owner = "order-platform-team", cost_centre = var.cost_centre }
}

module "platform" {
  source = "../../modules/platform"

  environment = local.environment
  location    = var.location
  tags        = local.tags

  event_topics = [
    "ordering-events",
    "pricing-events",
    "inventory-events",
    "payments-events",
    "shipping-events",
    "notifications-events",
  ]

  queues = {
    "ordering-commands"              = { sessions = true }
    "pricing-commands"               = { sessions = true }
    "inventory-commands"             = { sessions = true }
    "payments-commands"              = { sessions = true }
    "shipping-commands"              = { sessions = true }
    "notifications-delivery-reports" = { sessions = false } # Fed by Event Grid, which sets no session.
    "operations-resubmit"            = { sessions = false }
  }

  # Cost forecast, development and test: one B2 plan for every app. There is no demand to separate.
  # Basic plans have no deployment slots, so test releases deploy in place (staging_slots = false below).
  plans = {
    "shared" = { sku = "B2", instances = 1, zone_redundant = false }
  }

  log_daily_quota_gb = 2 # Escalator C1: a cap outside production.
  support_emails     = var.support_emails
  monthly_budget     = 200 # Cost forecast: about €125 a month for development and test.
  budget_start_date  = "2026-12-01T00:00:00Z"
}

module "ordering" {
  source = "../../modules/ordering-service"

  environment = local.environment
  tags        = local.tags
  platform    = module.platform

  # The same two apps as production, on the one test plan, so the split is tested before it matters.
  staging_slots = false
  apps = {
    intake = {
      plan      = "shared"
      functions = ["OnOrderingCommand"]
    }
    flow = {
      plan = "shared"
      functions = [
        "OnOrderSubmitted", "OnPriceQuoted", "OnPriceQuoteRejected", "OnStockReserved",
        "OnStockReservationFailed", "OnOrderPlaced", "OnPaymentAuthorized", "OnPaymentFailed",
        "OnOrderConfirmed", "OnShipmentDispatched", "OnShipmentDelivered", "OnShipmentFailed",
        "OnOrderRejected", "OnOrderCancelled",
      ]
    }
  }

  sql_admin_group           = var.sql_admin_group
  sql_sku                   = "S0"
  sql_backup_retention_days = 7
}

output "ordering_apps" {
  value = module.ordering.app_names
}

output "ordering_database" {
  value = {
    server   = module.ordering.sql_server_fqdn
    database = module.ordering.database_name
    users    = module.ordering.app_identity_names
  }
}
