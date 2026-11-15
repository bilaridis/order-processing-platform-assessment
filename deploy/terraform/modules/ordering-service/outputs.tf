output "resource_group_name" {
  value = azurerm_resource_group.ordering.name
}

output "app_names" {
  description = "Function app names by app key, for the release pipeline (one package, every app)."
  value       = { for key, app in module.app : key => app.name }
}

output "app_identity_names" {
  description = "Identity names, created as database users by the migration step, never by Terraform."
  value       = { for key, app in module.app : key => app.identity_name }
}

output "sql_server_fqdn" {
  value = azurerm_mssql_server.ordering.fully_qualified_domain_name
}

output "database_name" {
  value = azurerm_mssql_database.orders.name
}
