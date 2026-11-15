output "name" {
  value = azurerm_windows_function_app.this.name
}

output "id" {
  value = azurerm_windows_function_app.this.id
}

output "principal_id" {
  description = "The app's identity, for the role assignments of the service (queues, subscriptions, data)."
  value       = azurerm_user_assigned_identity.app.principal_id
}

output "client_id" {
  value = azurerm_user_assigned_identity.app.client_id
}

output "identity_name" {
  description = "The identity's name, used as the database user in Azure SQL (CREATE USER ... FROM EXTERNAL PROVIDER)."
  value       = azurerm_user_assigned_identity.app.name
}
