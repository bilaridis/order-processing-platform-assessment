variable "prefix" {
  type = string
}

variable "environment" {
  type = string
}

variable "name" {
  description = "The app's name inside the platform, for example \"ordering-intake\"."
  type        = string
}

variable "location" {
  type = string
}

variable "resource_group_name" {
  type = string
}

variable "service_plan_id" {
  description = "The plan of the app's demand tier (ADR-0010)."
  type        = string
}

variable "all_functions" {
  description = "Every function in the service's package (the [Function(\"...\")] names)."
  type        = list(string)
}

variable "functions" {
  description = "The functions that run in this app. The others are disabled by app setting."
  type        = list(string)

  validation {
    condition     = length(var.functions) > 0
    error_message = "An app must run at least one function."
  }
}

variable "service_bus_fqdn" {
  type = string
}

variable "application_insights_id" {
  type = string
}

variable "application_insights_connection_string" {
  type = string
}

variable "key_vault_id" {
  type = string
}

variable "app_settings" {
  description = "Settings of the service, for example a Key Vault reference or a database connection without a secret."
  type        = map(string)
  default     = {}
}

variable "dotnet_version" {
  description = "The .NET version of the isolated worker."
  type        = string
  default     = "v10.0"
}

variable "health_check_path" {
  description = "Health endpoint, if the app has one. Service Bus-only apps have none."
  type        = string
  default     = null
}

variable "tags" {
  type    = map(string)
  default = {}
}

variable "staging_slot" {
  description = "Deploy to a staging slot and swap (ADR-0011). Needs a Standard or Premium plan; Basic has no slots."
  type        = bool
  default     = true
}
