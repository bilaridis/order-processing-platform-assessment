variable "prefix" {
  type    = string
  default = "orders"
}

variable "environment" {
  type = string
}

variable "tags" {
  type    = map(string)
  default = {}
}

variable "platform" {
  description = "Outputs of the platform module: where the service plugs in."
  type = object({
    location                               = string
    service_bus_fqdn                       = string
    service_bus_namespace_name             = string
    service_bus_resource_group             = string
    topic_ids                              = map(string)
    queue_ids                              = map(string)
    plan_ids                               = map(string)
    application_insights_id                = string
    application_insights_connection_string = string
    key_vault_id                           = string
    log_analytics_workspace_id             = string
  })
}

variable "apps" {
  description = <<-EOT
    The function apps of the service, by name, with the demand tier (plan) each runs on and the
    functions it runs (ADR-0010). Every function of the service must run in exactly one app.
  EOT
  type = map(object({
    plan      = string
    functions = list(string)
  }))

  validation {
    condition     = length(flatten([for app in values(var.apps) : app.functions])) == length(distinct(flatten([for app in values(var.apps) : app.functions])))
    error_message = "A function runs in one app only; two apps would compete for its messages."
  }
}

variable "sql_admin_group" {
  description = "The Entra group that administers the database (the deployment identity is a member)."
  type = object({
    name      = string
    object_id = string
  })
}

variable "sql_sku" {
  description = "S2 at launch, General Purpose vCores later (cost forecast)."
  type        = string
  default     = "S2"
}

variable "sql_zone_redundant" {
  type    = bool
  default = false
}

variable "sql_backup_retention_days" {
  type    = number
  default = 7
}

variable "staging_slots" {
  description = "Release through staging slots. Off only where the plan is Basic (test)."
  type        = bool
  default     = true
}
