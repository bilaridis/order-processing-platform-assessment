# One Azure Functions app (isolated worker, Windows plan) with its staging slot. Every function app of
# the platform is made by this module, so they all have the same shape (ADR-0011):
# - a user-assigned identity shared by the app and its slot, so a swap never changes who the app is,
#   and role assignments are made once;
# - its own runtime storage account (the Functions host id is cut to 32 characters, so two apps of one
#   service sharing an account could collide), reached with the identity, no keys;
# - telemetry to the shared Application Insights with Entra authentication;
# - the package setting ignored: Terraform provisions, the pipeline releases.
#
# One package per service, several apps (ADR-0010): every app of a service gets the same package, and
# the settings "AzureWebJobs.<function>.Disabled" switch off the functions that run in another app. Moving
# a function to another plan is a change of input, not of code.

locals {
  runtime_storage_name = lower(substr(
    "st${replace("${var.prefix}${var.environment}${var.name}", "-", "")}", 0, 16
  ))
  storage_account_name = "${local.runtime_storage_name}${substr(md5("${var.environment}-${var.name}"), 0, 8)}"

  # In this app: its own functions on, the functions that run in the other apps of the service off.
  function_switches = {
    for function in var.all_functions :
    "AzureWebJobs.${function}.Disabled" => contains(var.functions, function) ? "false" : "true"
  }

  # In the staging slot: every function off, so a new version takes no message before the swap.
  # These are slot settings, so they stay with the slot and production keeps its own values.
  staging_switches = {
    for function in var.all_functions : "AzureWebJobs.${function}.Disabled" => "true"
  }

  app_settings = merge(
    {
      # Runtime storage with the user-assigned identity (the account name is set by the provider).
      "AzureWebJobsStorage__credential" = "managedidentity"
      "AzureWebJobsStorage__clientId"   = azurerm_user_assigned_identity.app.client_id

      # Service Bus triggers and the senders in code read the same setting (ServiceBusMessaging.cs).
      "ServiceBus__fullyQualifiedNamespace" = var.service_bus_fqdn
      "ServiceBus__credential"              = "managedidentity"
      "ServiceBus__clientId"                = azurerm_user_assigned_identity.app.client_id

      # DefaultAzureCredential in the code picks the user-assigned identity with this.
      "AZURE_CLIENT_ID" = azurerm_user_assigned_identity.app.client_id

      # Telemetry with Entra authentication (local authentication is off on Application Insights).
      "APPLICATIONINSIGHTS_AUTHENTICATION_STRING" = "ClientId=${azurerm_user_assigned_identity.app.client_id};Authorization=AAD"
    },
    local.function_switches,
    var.app_settings,
  )
}

resource "azurerm_user_assigned_identity" "app" {
  name                = "id-${var.prefix}-${var.environment}-${var.name}"
  location            = var.location
  resource_group_name = var.resource_group_name
  tags                = var.tags
}

resource "azurerm_storage_account" "runtime" {
  name                            = local.storage_account_name
  location                        = var.location
  resource_group_name             = var.resource_group_name
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  shared_access_key_enabled       = false # Identity only.
  allow_nested_items_to_be_public = false
  tags                            = var.tags

  blob_properties {
    delete_retention_policy {
      days = 7
    }
    container_delete_retention_policy {
      days = 7
    }
  }
}

resource "azurerm_role_assignment" "runtime_storage" {
  for_each = toset([
    "Storage Blob Data Owner",
    "Storage Queue Data Contributor",
    "Storage Table Data Contributor",
  ])

  scope                = azurerm_storage_account.runtime.id
  role_definition_name = each.value
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

resource "azurerm_role_assignment" "telemetry" {
  scope                = var.application_insights_id
  role_definition_name = "Monitoring Metrics Publisher"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

resource "azurerm_role_assignment" "key_vault_secrets" {
  scope                = var.key_vault_id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

resource "azurerm_windows_function_app" "this" {
  name                = "func-${var.prefix}-${var.environment}-${var.name}"
  location            = var.location
  resource_group_name = var.resource_group_name
  service_plan_id     = var.service_plan_id

  https_only                      = true
  public_network_access_enabled   = true # Service Bus-triggered: nothing calls it. Inbound is closed below.
  key_vault_reference_identity_id = azurerm_user_assigned_identity.app.id
  storage_account_name            = azurerm_storage_account.runtime.name
  storage_uses_managed_identity   = true
  app_settings                    = local.app_settings
  tags                            = var.tags

  sticky_settings {
    app_setting_names = keys(local.function_switches)
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  site_config {
    always_on                              = true # Dedicated plan: triggers must keep listening.
    ftps_state                             = "Disabled"
    minimum_tls_version                    = "1.2"
    application_insights_connection_string = var.application_insights_connection_string
    health_check_path                      = var.health_check_path
    health_check_eviction_time_in_min      = var.health_check_path == null ? null : 5

    application_stack {
      dotnet_version              = var.dotnet_version
      use_dotnet_isolated_runtime = true
    }

    # No HTTP triggers in a service (ADR-0003): nothing may reach the app from outside.
    ip_restriction_default_action = "Deny"
  }

  lifecycle {
    # Set by the release pipeline (zip deploy, run from package). Terraform must not undo a release.
    ignore_changes = [app_settings["WEBSITE_RUN_FROM_PACKAGE"]]
  }
}

resource "azurerm_windows_function_app_slot" "staging" {
  count = var.staging_slot ? 1 : 0

  name            = "staging"
  function_app_id = azurerm_windows_function_app.this.id

  https_only                      = true
  public_network_access_enabled   = true
  key_vault_reference_identity_id = azurerm_user_assigned_identity.app.id
  storage_account_name            = azurerm_storage_account.runtime.name
  storage_uses_managed_identity   = true
  # The new version is warmed up and health-checked here but processes no message until the swap.
  app_settings = merge(local.app_settings, local.staging_switches)
  tags         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  site_config {
    always_on                              = true
    ftps_state                             = "Disabled"
    minimum_tls_version                    = "1.2"
    application_insights_connection_string = var.application_insights_connection_string

    application_stack {
      dotnet_version              = var.dotnet_version
      use_dotnet_isolated_runtime = true
    }

    ip_restriction_default_action = "Deny"
  }

  lifecycle {
    ignore_changes = [app_settings["WEBSITE_RUN_FROM_PACKAGE"]]
  }
}
