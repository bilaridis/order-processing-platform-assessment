# Plan-only tests with a mocked provider: no Azure subscription needed, runs in CI on every pull request.
# They check the rules of ADR-0010 and ADR-0003 that a typo in an environment would break silently.

mock_provider "azurerm" {}

variables {
  environment = "unit"
  platform = {
    location                               = "westeurope"
    service_bus_fqdn                       = "sb-orders-unit.servicebus.windows.net"
    service_bus_namespace_name             = "sb-orders-unit"
    service_bus_resource_group             = "rg-orders-unit-platform"
    application_insights_id                = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Insights/components/appi"
    application_insights_connection_string = "InstrumentationKey=00000000-0000-0000-0000-000000000000"
    log_analytics_workspace_id             = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/log"
    key_vault_id                           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv"
    plan_ids = {
      "order-intake" = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Web/serverFarms/intake"
      "order-flow"   = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Web/serverFarms/flow"
    }
    topic_ids = {
      for topic in ["ordering-events", "pricing-events", "inventory-events", "payments-events", "shipping-events"] :
      topic => "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.ServiceBus/namespaces/sb/topics/${topic}"
    }
    queue_ids = {
      for queue in ["ordering-commands", "pricing-commands", "inventory-commands", "payments-commands", "shipping-commands"] :
      queue => "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.ServiceBus/namespaces/sb/queues/${queue}"
    }
  }
  sql_admin_group = { name = "sql-admins", object_id = "00000000-0000-0000-0000-000000000001" }
  apps = {
    intake = { plan = "order-intake", functions = ["OnOrderingCommand"] }
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
}

run "each_function_has_a_filtered_session_subscription" {
  command = plan

  assert {
    condition     = length(azurerm_servicebus_subscription.this) == 14
    error_message = "One subscription per event function."
  }
  assert {
    condition     = alltrue([for s in azurerm_servicebus_subscription.this : s.requires_session])
    error_message = "Subscriptions must use sessions: one order's events in order."
  }
  assert {
    condition     = azurerm_servicebus_subscription_rule.subject["OnPriceQuoted"].correlation_filter[0].label == "PriceQuoted"
    error_message = "A subscription filters on the event's type name."
  }
}

run "apps_receive_only_what_their_functions_listen_to" {
  command = plan

  assert {
    condition     = contains(keys(azurerm_role_assignment.receive), "intake/ordering-commands")
    error_message = "The intake app receives the command queue."
  }
  assert {
    condition     = !contains(keys(azurerm_role_assignment.receive), "flow/ordering-commands")
    error_message = "The flow app must not receive commands."
  }
  assert {
    condition     = length([for k in keys(azurerm_role_assignment.receive) : k if startswith(k, "flow/")]) == 14
    error_message = "The flow app receives its 14 subscriptions and nothing else."
  }
}

run "a_function_in_two_apps_is_rejected" {
  command = plan

  variables {
    apps = {
      intake = { plan = "order-intake", functions = ["OnOrderingCommand", "OnOrderPlaced"] }
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
  }

  expect_failures = [var.apps]
}

run "a_function_in_no_app_is_rejected" {
  command = plan

  variables {
    apps = {
      intake = { plan = "order-intake", functions = ["OnOrderingCommand"] }
    }
  }

  expect_failures = [azurerm_resource_group.ordering]
}
