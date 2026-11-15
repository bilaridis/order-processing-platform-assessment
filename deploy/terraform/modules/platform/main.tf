# The shared platform of one environment: what every service uses and no service owns.
# - Service Bus namespace with the published messaging contract: one topic per publishing service and one
#   command queue per service (ADR-0003). Subscriptions belong to the consuming service and are created by
#   its own module, so a service can be added without touching this one.
# - The App Service plans drawn by demand (ADR-0010).
# - Telemetry: one Log Analytics workspace and one Application Insights resource (ADR-0008).
# - Key Vault for the few secrets that exist (provider API keys, webhook secrets) (ADR-0007).
# - The alert that matters most without an outbox: any dead-lettered message (ADR-0012).

locals {
  name = "${var.prefix}-${var.environment}"
  tags = merge(var.tags, { environment = var.environment, system = "order-processing" })
}

data "azurerm_client_config" "current" {}

resource "azurerm_resource_group" "platform" {
  name     = "rg-${local.name}-platform"
  location = var.location
  tags     = local.tags
}

# ---------------------------------------------------------------------------------------------------
# Telemetry
# ---------------------------------------------------------------------------------------------------

resource "azurerm_log_analytics_workspace" "this" {
  name                = "log-${local.name}"
  location            = var.location
  resource_group_name = azurerm_resource_group.platform.name
  sku                 = "PerGB2018"
  retention_in_days   = var.log_retention_days
  daily_quota_gb      = var.log_daily_quota_gb # Escalator C1: a cap on non-production, -1 (none) in production.
  tags                = local.tags
}

resource "azurerm_application_insights" "this" {
  name                = "appi-${local.name}"
  location            = var.location
  resource_group_name = azurerm_resource_group.platform.name
  workspace_id        = azurerm_log_analytics_workspace.this.id
  application_type    = "web"
  # Apps send telemetry with their managed identity (Monitoring Metrics Publisher), not with the key.
  local_authentication_enabled = false
  tags                         = local.tags
}

# ---------------------------------------------------------------------------------------------------
# Messaging
# ---------------------------------------------------------------------------------------------------

resource "azurerm_servicebus_namespace" "this" {
  name                = "sb-${local.name}"
  location            = var.location
  resource_group_name = azurerm_resource_group.platform.name
  sku                 = var.service_bus_sku # Standard at launch; Premium only if private messaging is required.
  local_auth_enabled  = false               # Entra identities only; no shared access keys.
  minimum_tls_version = "1.2"
  tags                = local.tags

  identity {
    type = "SystemAssigned" # Needed only for a customer-managed key later; costs nothing.
  }
}

# Events: one topic per publishing service, named "<service>-events" (ServiceBusMessages.TopicFor).
resource "azurerm_servicebus_topic" "events" {
  for_each = toset(var.event_topics)

  name                                    = each.value
  namespace_id                            = azurerm_servicebus_namespace.this.id
  requires_duplicate_detection            = true # The message id is the deterministic event id (ADR-0012).
  duplicate_detection_history_time_window = "PT1H"
  default_message_ttl                     = "P14D"
}

# Commands: one queue per service. Command queues are session-enabled, the session is the order id.
resource "azurerm_servicebus_queue" "this" {
  for_each = var.queues

  name                                    = each.key
  namespace_id                            = azurerm_servicebus_namespace.this.id
  requires_session                        = each.value.sessions
  requires_duplicate_detection            = true
  duplicate_detection_history_time_window = "PT1H"
  max_delivery_count                      = 10
  lock_duration                           = "PT1M"
  default_message_ttl                     = "P14D"
  dead_lettering_on_message_expiration    = true # An expired command is a stuck order, never a silent loss.
}

# ---------------------------------------------------------------------------------------------------
# Compute: plans drawn by demand (ADR-0010). Windows plans, the team lead's choice (ADR-0009).
# ---------------------------------------------------------------------------------------------------

resource "azurerm_service_plan" "this" {
  for_each = var.plans

  name                   = "asp-${local.name}-${each.key}"
  location               = var.location
  resource_group_name    = azurerm_resource_group.platform.name
  os_type                = "Windows"
  sku_name               = each.value.sku
  worker_count           = each.value.instances
  zone_balancing_enabled = each.value.zone_redundant
  tags                   = local.tags
}

# ---------------------------------------------------------------------------------------------------
# Secrets
# ---------------------------------------------------------------------------------------------------

resource "azurerm_key_vault" "this" {
  name                          = "kv-${local.name}"
  location                      = var.location
  resource_group_name           = azurerm_resource_group.platform.name
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true # Access by role assignment, per app identity.
  purge_protection_enabled      = true
  soft_delete_retention_days    = 90
  public_network_access_enabled = true # Launch position: public endpoint, Entra-only (05, "The cost of going private").
  tags                          = local.tags
}

# ---------------------------------------------------------------------------------------------------
# Alerts L1 support acts on (ADR-0008)
# ---------------------------------------------------------------------------------------------------

resource "azurerm_monitor_action_group" "support" {
  name                = "ag-${local.name}-support"
  resource_group_name = azurerm_resource_group.platform.name
  short_name          = "l1support"
  tags                = local.tags

  dynamic "email_receiver" {
    for_each = var.support_emails
    content {
      name                    = "support-${email_receiver.key}"
      email_address           = email_receiver.value
      use_common_alert_schema = true
    }
  }
}

# Without an outbox, a dead-lettered message is an order that stopped (ADR-0012). One is enough to alert.
# The alert is split by entity, so it names the queue or subscription; the dead-letter inspector in the
# operations app says which order and why.
resource "azurerm_monitor_metric_alert" "dead_letters" {
  name                = "alert-${local.name}-dead-letters"
  resource_group_name = azurerm_resource_group.platform.name
  scopes              = [azurerm_servicebus_namespace.this.id]
  description         = "A message was dead-lettered: an order has stopped. Open the dead-letter view in Grafana."
  severity            = 1
  frequency           = "PT5M"
  window_size         = "PT5M"
  tags                = local.tags

  criteria {
    metric_namespace = "Microsoft.ServiceBus/namespaces"
    metric_name      = "DeadletteredMessages"
    aggregation      = "Maximum"
    operator         = "GreaterThan"
    threshold        = 0

    dimension {
      name     = "EntityName"
      operator = "Include"
      values   = ["*"]
    }
  }

  action {
    action_group_id = azurerm_monitor_action_group.support.id
  }
}

# Orders waiting: messages piling up on a command queue mean a service is not keeping up or is down.
resource "azurerm_monitor_metric_alert" "backlog" {
  name                = "alert-${local.name}-backlog"
  resource_group_name = azurerm_resource_group.platform.name
  scopes              = [azurerm_servicebus_namespace.this.id]
  description         = "Messages are waiting on a queue: a service is slow or down and customers wait in Submitted."
  severity            = 2
  frequency           = "PT5M"
  window_size         = "PT15M"
  tags                = local.tags

  criteria {
    metric_namespace = "Microsoft.ServiceBus/namespaces"
    metric_name      = "ActiveMessages"
    aggregation      = "Average"
    operator         = "GreaterThan"
    threshold        = var.backlog_alert_threshold

    dimension {
      name     = "EntityName"
      operator = "Include"
      values   = ["*"]
    }
  }

  action {
    action_group_id = azurerm_monitor_action_group.support.id
  }
}

# Escalators in the cost forecast are only useful if someone hears about them.
resource "azurerm_consumption_budget_resource_group" "platform" {
  name              = "budget-${local.name}"
  resource_group_id = azurerm_resource_group.platform.id
  amount            = var.monthly_budget
  time_grain        = "Monthly"

  time_period {
    start_date = var.budget_start_date
  }

  notification {
    enabled        = true
    threshold      = 80
    operator       = "GreaterThan"
    threshold_type = "Forecasted"
    contact_groups = [azurerm_monitor_action_group.support.id]
  }
}
