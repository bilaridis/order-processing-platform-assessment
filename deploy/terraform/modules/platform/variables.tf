variable "prefix" {
  description = "Short name of the system, used in every resource name."
  type        = string
  default     = "orders"
}

variable "environment" {
  description = "Environment name: test, staging or production."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
  default     = "westeurope"
}

variable "tags" {
  description = "Tags added to every resource (cost centre, owner)."
  type        = map(string)
  default     = {}
}

variable "event_topics" {
  description = "One topic per publishing service, \"<service>-events\". The topics are the published contract."
  type        = list(string)
}

variable "queues" {
  description = "Queues by name. Command queues use sessions keyed by order id; queues fed by Azure (Event Grid delivery reports) and the operations resubmit queue do not."
  type = map(object({
    sessions = bool
  }))
}

variable "plans" {
  description = "App Service plans by demand tier (ADR-0010). Keys are the tier names the apps refer to."
  type = map(object({
    sku            = string
    instances      = number
    zone_redundant = bool
  }))
}

variable "service_bus_sku" {
  description = "Standard at launch. Premium is the price of a private endpoint for Service Bus (cost forecast, step 3)."
  type        = string
  default     = "Standard"
}

variable "log_retention_days" {
  type    = number
  default = 30
}

variable "log_daily_quota_gb" {
  description = "Daily ingestion cap. Set on non-production; -1 means no cap."
  type        = number
  default     = -1
}

variable "support_emails" {
  description = "Who the L1 support alerts go to."
  type        = list(string)
}

variable "backlog_alert_threshold" {
  description = "Messages waiting on one queue before L1 support is alerted."
  type        = number
  default     = 100
}

variable "monthly_budget" {
  description = "Monthly budget for the environment in the billing currency."
  type        = number
}

variable "budget_start_date" {
  description = "First day of the budget, RFC 3339, the first of a month."
  type        = string
}
