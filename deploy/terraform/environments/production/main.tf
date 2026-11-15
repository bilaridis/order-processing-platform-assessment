# Production. One root configuration per environment (ADR-0011); the environments differ only in the
# values below, never in structure. Applied only by the pipeline, from main, after approval of the plan.

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
    storage_account_name = "stordersprodtfstate"
    container_name       = "tfstate"
    key                  = "production.tfstate"
    use_azuread_auth     = true # No storage key: the pipeline's identity reads and locks the state.
  }
}

provider "azurerm" {
  features {}
  storage_use_azuread = true
  # subscription_id comes from ARM_SUBSCRIPTION_ID, set by the pipeline (OIDC login).
}

locals {
  environment = "production"
  tags        = { owner = "order-platform-team", cost_centre = var.cost_centre }
}

module "platform" {
  source = "../../modules/platform"

  environment = local.environment
  location    = var.location
  tags        = local.tags

  log_retention_days = 90 # ADR-0014: telemetry is kept 90 days.

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

  # ADR-0010, launch position: order intake on its own plan; order flow and on-demand work share the
  # second plan until they need to scale apart. P0v3 is the smallest size with slots and zone balancing.
  plans = {
    # Zone balancing needs at least two instances, which the launch position already has: no extra cost.
    "order-intake" = { sku = "P0v3", instances = 2, zone_redundant = true }
    "order-flow"   = { sku = "P0v3", instances = 2, zone_redundant = true }
  }

  support_emails    = var.support_emails
  monthly_budget    = 800 # Cost forecast: about €610 at launch. Alert at 80% of forecast spend.
  budget_start_date = "2026-12-01T00:00:00Z"
}

module "ordering" {
  source = "../../modules/ordering-service"

  environment = local.environment
  tags        = local.tags
  platform    = module.platform

  # ADR-0010. Commands (place and cancel) on the intake plan; the order flow after acceptance on the
  # order-flow plan. A function moves between apps by moving its name between these lists.
  apps = {
    intake = {
      plan      = "order-intake"
      functions = ["OnOrderingCommand"]
    }
    flow = {
      plan = "order-flow"
      functions = [
        "OnOrderSubmitted", "OnPriceQuoted", "OnPriceQuoteRejected", "OnStockReserved",
        "OnStockReservationFailed", "OnOrderPlaced", "OnPaymentAuthorized", "OnPaymentFailed",
        "OnOrderConfirmed", "OnShipmentDispatched", "OnShipmentDelivered", "OnShipmentFailed",
        "OnOrderRejected", "OnOrderCancelled",
      ]
    }
  }

  sql_admin_group           = var.sql_admin_group
  sql_sku                   = "S2"
  sql_backup_retention_days = 14
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
