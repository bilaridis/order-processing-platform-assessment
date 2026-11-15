# The Ordering service: everything it owns in one environment. This module is the pattern the other
# services copy: its apps (one per demand tier it has features in), its subscriptions, its data and the
# roles of its identities. Nothing here is shared with another service (ADR-0001, ADR-0004).
#
# The function names, subscriptions and subjects below are the ones in
# src/Modules/Ordering/OrderProcessing.Ordering.Functions. A function added there is added here too:
# one line in local.functions.

locals {
  name = "${var.prefix}-${var.environment}"
  tags = merge(var.tags, { environment = var.environment, system = "order-processing", service = "ordering" })

  # Every function of the service, and the entity that triggers it. Subscriptions filter on the message
  # subject, which is the event's type name (ServiceBusMessages.Create).
  command_function = "OnOrderingCommand"
  functions = {
    OnOrderSubmitted         = { subscription = "ordering-on-order-submitted", topic = "ordering-events", subject = "OrderSubmitted" }
    OnPriceQuoted            = { subscription = "ordering-on-price-quoted", topic = "pricing-events", subject = "PriceQuoted" }
    OnPriceQuoteRejected     = { subscription = "ordering-on-price-quote-rejected", topic = "pricing-events", subject = "PriceQuoteRejected" }
    OnStockReserved          = { subscription = "ordering-on-stock-reserved", topic = "inventory-events", subject = "StockReserved" }
    OnStockReservationFailed = { subscription = "ordering-on-stock-reservation-failed", topic = "inventory-events", subject = "StockReservationFailed" }
    OnOrderPlaced            = { subscription = "ordering-on-order-placed", topic = "ordering-events", subject = "OrderPlaced" }
    OnPaymentAuthorized      = { subscription = "ordering-on-payment-authorized", topic = "payments-events", subject = "PaymentAuthorized" }
    OnPaymentFailed          = { subscription = "ordering-on-payment-failed", topic = "payments-events", subject = "PaymentFailed" }
    OnOrderConfirmed         = { subscription = "ordering-on-order-confirmed", topic = "ordering-events", subject = "OrderConfirmed" }
    OnShipmentDispatched     = { subscription = "ordering-on-shipment-dispatched", topic = "shipping-events", subject = "ShipmentDispatched" }
    OnShipmentDelivered      = { subscription = "ordering-on-shipment-delivered", topic = "shipping-events", subject = "ShipmentDelivered" }
    OnShipmentFailed         = { subscription = "ordering-on-shipment-failed", topic = "shipping-events", subject = "ShipmentFailed" }
    OnOrderRejected          = { subscription = "ordering-on-order-rejected", topic = "ordering-events", subject = "OrderRejected" }
    OnOrderCancelled         = { subscription = "ordering-on-order-cancelled", topic = "ordering-events", subject = "OrderCancelled" }
  }
  all_functions = concat([local.command_function], keys(local.functions))

  # What the service sends: its own events, and commands to the services it asks for work (ADR-0003).
  # Granted per service, not per app: the apps share one codebase and one release.
  own_topic        = "ordering-events"
  command_queue    = "ordering-commands"
  commands_sent_to = ["pricing-commands", "inventory-commands", "payments-commands", "shipping-commands"]

  # Who receives what, per app: the command queue if the app runs the command function, and the
  # subscription of each event function it runs.
  receives = merge([
    for app_name, app in var.apps : merge(
      contains(app.functions, local.command_function)
      ? { "${app_name}/${local.command_queue}" = { app = app_name, scope = var.platform.queue_ids[local.command_queue] } }
      : {},
      {
        for function in app.functions :
        "${app_name}/${local.functions[function].subscription}" => {
          app   = app_name
          scope = azurerm_servicebus_subscription.this[function].id
        } if function != local.command_function
      },
    )
  ]...)

  sends = merge([
    for app_name, _ in var.apps : {
      for entity in concat([local.own_topic], local.commands_sent_to) :
      "${app_name}/${entity}" => {
        app   = app_name
        scope = entity == local.own_topic ? var.platform.topic_ids[entity] : var.platform.queue_ids[entity]
      }
    }
  ]...)
}

resource "azurerm_resource_group" "ordering" {
  name     = "rg-${local.name}-ordering"
  location = var.platform.location
  tags     = local.tags

  lifecycle {
    precondition {
      condition     = length(setsubtract(local.all_functions, flatten([for app in values(var.apps) : app.functions]))) == 0
      error_message = "Every function of the service must run in an app; otherwise its messages pile up unseen."
    }
    precondition {
      condition     = length(setsubtract(flatten([for app in values(var.apps) : app.functions]), local.all_functions)) == 0
      error_message = "An app lists a function that the Ordering package does not have."
    }
  }
}

# ---------------------------------------------------------------------------------------------------
# Apps: one per demand tier the service has features in (ADR-0010), all from the same package.
# ---------------------------------------------------------------------------------------------------

module "app" {
  source   = "../function-app"
  for_each = var.apps

  prefix              = var.prefix
  environment         = var.environment
  name                = "ordering-${each.key}"
  location            = var.platform.location
  resource_group_name = azurerm_resource_group.ordering.name
  service_plan_id     = var.platform.plan_ids[each.value.plan]
  staging_slot        = var.staging_slots

  all_functions = local.all_functions
  functions     = each.value.functions

  service_bus_fqdn                       = var.platform.service_bus_fqdn
  application_insights_id                = var.platform.application_insights_id
  application_insights_connection_string = var.platform.application_insights_connection_string
  key_vault_id                           = var.platform.key_vault_id

  app_settings = {
    # Controlled SQL execution (05): the app's identity is a database user with data rights only. No
    # password, no schema rights; migrations run in the pipeline under the deployment identity.
    # "Active Directory Default" uses the app's identity through AZURE_CLIENT_ID (function-app module).
    "ConnectionStrings__Ordering" = "Server=tcp:${azurerm_mssql_server.ordering.fully_qualified_domain_name},1433;Database=${azurerm_mssql_database.orders.name};Authentication=Active Directory Default"
  }

  tags = local.tags
}

# ---------------------------------------------------------------------------------------------------
# Subscriptions: owned by the consumer, one per function, filtered on the subject.
# ---------------------------------------------------------------------------------------------------

resource "azurerm_servicebus_subscription" "this" {
  for_each = local.functions

  name                                      = each.value.subscription
  topic_id                                  = var.platform.topic_ids[each.value.topic]
  requires_session                          = true # Session = order id: one order's events in order.
  max_delivery_count                        = 10
  lock_duration                             = "PT1M"
  dead_lettering_on_message_expiration      = true
  dead_lettering_on_filter_evaluation_error = true
}

resource "azurerm_servicebus_subscription_rule" "subject" {
  for_each = local.functions

  name            = "subject"
  subscription_id = azurerm_servicebus_subscription.this[each.key].id
  filter_type     = "CorrelationFilter"

  correlation_filter {
    label = each.value.subject # "label" is the subject of the message.
  }
}

# A new subscription starts with a "$Default" rule that accepts every message, and rules are combined
# with OR, so it would let every event of the topic through next to the filter above. It is removed here.
# The command is idempotent: it succeeds whether or not Azure created the rule.
resource "terraform_data" "remove_default_rule" {
  for_each = local.functions

  triggers_replace = [azurerm_servicebus_subscription_rule.subject[each.key].id]

  provisioner "local-exec" {
    command = "az servicebus topic subscription rule delete --resource-group ${var.platform.service_bus_resource_group} --namespace-name ${var.platform.service_bus_namespace_name} --topic-name ${each.value.topic} --subscription-name ${each.value.subscription} --name '$Default' --only-show-errors || true"
  }
}

# ---------------------------------------------------------------------------------------------------
# Roles of the apps' identities: receive only what each app's functions listen to; send only to the
# service's own topic and to the queues of the services it gives work to.
# ---------------------------------------------------------------------------------------------------

resource "azurerm_role_assignment" "receive" {
  for_each = local.receives

  scope                = each.value.scope
  role_definition_name = "Azure Service Bus Data Receiver"
  principal_id         = module.app[each.value.app].principal_id
}

resource "azurerm_role_assignment" "send" {
  for_each = local.sends

  scope                = each.value.scope
  role_definition_name = "Azure Service Bus Data Sender"
  principal_id         = module.app[each.value.app].principal_id
}

# ---------------------------------------------------------------------------------------------------
# Data of record: Azure SQL, owned by Ordering alone (ADR-0004), Entra authentication only.
# ---------------------------------------------------------------------------------------------------

resource "azurerm_mssql_server" "ordering" {
  name                          = "sql-${local.name}-ordering"
  location                      = var.platform.location
  resource_group_name           = azurerm_resource_group.ordering.name
  version                       = "12.0"
  minimum_tls_version           = "1.2"
  public_network_access_enabled = true # Launch position: Azure services only (rule below), Entra only.
  tags                          = local.tags

  azuread_administrator {
    login_username              = var.sql_admin_group.name
    object_id                   = var.sql_admin_group.object_id
    azuread_authentication_only = true # No SQL logins, no passwords.
  }
}

# "Allow Azure services": the apps reach SQL without private endpoints at launch (05, step 1).
# Step 2 of "The cost of going private" replaces this with a private endpoint.
resource "azurerm_mssql_firewall_rule" "azure_services" {
  name             = "AllowAzureServices"
  server_id        = azurerm_mssql_server.ordering.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_mssql_database" "orders" {
  name                 = "orders"
  server_id            = azurerm_mssql_server.ordering.id
  sku_name             = var.sql_sku
  zone_redundant       = var.sql_zone_redundant
  storage_account_type = var.sql_zone_redundant ? "Zone" : "Local"
  tags                 = local.tags

  short_term_retention_policy {
    retention_days = var.sql_backup_retention_days
  }

  lifecycle {
    prevent_destroy = true # The data of record. Deleting it takes a change to this line, reviewed.
  }
}

# Who read or changed what in the data of record: audit to Log Analytics, kept with the other logs.
resource "azurerm_mssql_server_extended_auditing_policy" "ordering" {
  server_id              = azurerm_mssql_server.ordering.id
  log_monitoring_enabled = true
  retention_in_days      = 0 # Retention is the workspace's (log_retention_days), not the policy's.
}

resource "azurerm_monitor_diagnostic_setting" "sql_audit" {
  name                       = "audit-to-log-analytics"
  target_resource_id         = "${azurerm_mssql_server.ordering.id}/databases/master"
  log_analytics_workspace_id = var.platform.log_analytics_workspace_id

  enabled_log {
    category = "SQLSecurityAuditEvents"
  }

  depends_on = [azurerm_mssql_server_extended_auditing_policy.ordering]
}
