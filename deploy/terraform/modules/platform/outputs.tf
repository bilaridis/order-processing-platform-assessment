output "location" {
  value = azurerm_resource_group.platform.location
}

output "service_bus_namespace_id" {
  value = azurerm_servicebus_namespace.this.id
}

output "service_bus_namespace_name" {
  value = azurerm_servicebus_namespace.this.name
}

output "service_bus_resource_group" {
  value = azurerm_resource_group.platform.name
}

output "service_bus_fqdn" {
  description = "The value of the app setting ServiceBus__fullyQualifiedNamespace."
  value       = "${azurerm_servicebus_namespace.this.name}.servicebus.windows.net"
}

output "topic_ids" {
  description = "Topic ids by name, for the subscriptions of the consuming services."
  value       = { for name, topic in azurerm_servicebus_topic.events : name => topic.id }
}

output "queue_ids" {
  description = "Queue ids by name, for the role assignments of senders and receivers."
  value       = { for name, queue in azurerm_servicebus_queue.this : name => queue.id }
}

output "plan_ids" {
  description = "App Service plan ids by demand tier."
  value       = { for tier, plan in azurerm_service_plan.this : tier => plan.id }
}

output "application_insights_id" {
  value = azurerm_application_insights.this.id
}

output "application_insights_connection_string" {
  description = "Not a secret once local authentication is off: it only says where to send telemetry."
  value       = azurerm_application_insights.this.connection_string
}

output "key_vault_id" {
  value = azurerm_key_vault.this.id
}

output "key_vault_uri" {
  value = azurerm_key_vault.this.vault_uri
}

output "log_analytics_workspace_id" {
  value = azurerm_log_analytics_workspace.this.id
}
