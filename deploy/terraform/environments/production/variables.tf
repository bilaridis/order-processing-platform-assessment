variable "location" {
  type    = string
  default = "westeurope"
}

variable "cost_centre" {
  type = string
}

variable "support_emails" {
  description = "L1 support distribution list."
  type        = list(string)
}

variable "sql_admin_group" {
  description = "Entra group that administers Azure SQL; the pipeline's deployment identity is a member."
  type = object({
    name      = string
    object_id = string
  })
}
