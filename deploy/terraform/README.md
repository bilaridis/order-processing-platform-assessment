# Terraform

The infrastructure as code of [ADR-0011](../../docs/adr/0011-deployment.md): the shared platform and
one service, **Ordering**, end to end. The other services are built the same way: copy
`modules/ordering-service`, change its function list, its subscriptions and what it sends, and add it to
each environment. Terraform provisions; the release pipelines deploy the code.

```
deploy/terraform/
  modules/
    platform/           Service Bus (topics and command queues), App Service plans by demand,
                        Log Analytics + Application Insights, Key Vault, L1 alerts, budget
    function-app/       One Functions app with its identity, runtime storage, telemetry and staging slot.
                        Every app of the platform is made by this module.
    ordering-service/   Ordering: its apps, subscriptions, roles and Azure SQL database
      tests/            Plan-only tests with a mocked provider (terraform test)
  environments/
    test/               One B2 plan, no slots, capped telemetry
    production/         Two plans (order intake, order flow), zone balanced, staging slots
  .checkov.yaml         Security scan, with every skipped check and the decision behind it
```

## What the code shows

| Decision | Where |
|----------|-------|
| Service Bus only between services; topic per publisher, queue per service, subscription per consumer function (ADR-0003) | `platform/main.tf` creates the published contract (topics and command queues). The consumer owns its subscriptions: `ordering-service/main.tf`. |
| Subscriptions filter on the event's type name, with sessions keyed by order id | `azurerm_servicebus_subscription_rule.subject` (correlation filter on the subject). The catch-all `$Default` rule is removed, or every event of the topic would arrive. |
| No outbox: a dead letter is a stopped order (ADR-0012) | `azurerm_monitor_metric_alert.dead_letters`: one dead-lettered message alerts L1 support, split by entity. Expired messages dead-letter instead of disappearing. |
| Plans by demand, not by service (ADR-0010) | `plans` in each environment. An app names its tier; moving an app is a one-word change. |
| One package, several apps per service (ADR-0010) | `function-app/main.tf`: every app gets the whole package; the settings `AzureWebJobs.<function>.Disabled` switch off what runs elsewhere. `ordering-service` refuses a function in two apps or in none. |
| Zero-downtime releases (ADR-0011) | Staging slot with every function switched off as a slot setting, so the new version takes no message before the swap. |
| Identities, no secrets (ADR-0007) | One user-assigned identity per app, shared with its slot. No storage keys, no SQL logins, no Service Bus keys, Application Insights with Entra authentication. |
| Least privilege on messaging | An app receives only the queue or subscriptions its functions listen to, and sends only to its service's topic and to the queues of the services it gives work to. Tested in `tests/`. |
| Controlled SQL execution (05) | Entra-only Azure SQL. App identities are database users with data rights only, created by the migration step, never by Terraform. Audit to Log Analytics. `prevent_destroy` on the database. |
| Release is not an infrastructure change | The package setting is ignored by Terraform; `.github/workflows/release-ordering.yml` deploys it. |
| Launch position on private networking | Public endpoints, Entra only. Each deferred private endpoint is a skipped check in `.checkov.yaml`, with its step in the cost forecast. |

## Pipelines

- [`terraform.yml`](../../.github/workflows/terraform.yml): on a pull request, format, validate,
  module tests and the Checkov scan, then the plan for test posted on the pull request. On main, apply
  to test. Production is started by hand: the plan is saved, approved in the `production` environment,
  and exactly that plan is applied.
- [`release-ordering.yml`](../../.github/workflows/release-ordering.yml): build and test once, one zip
  package, deployed to every app of the service. Test on every merge to main; production by hand with the
  run id of a test release, to the staging slots, then swapped.

## Before the first apply

These are done once per environment, by a person with Owner rights, and are not in this configuration:

1. A storage account for the state (`rg-orders-tfstate`), versioning on, access only for the pipeline identity.
2. An Entra application for the pipeline with a federated credential for this repository's environments
   (`test`, `test-plan`, `production`, `production-plan`), and Owner on the subscription (it assigns roles).
3. An Entra group for SQL administration, with the pipeline identity as a member.
4. Repository variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`; secrets
   `TFVARS_TEST` and `TFVARS_PRODUCTION` with the content of the `*.tfvars.example` files.
5. Required reviewers on the `production` environment.

## Not verified

The configuration passes `terraform validate`, the module tests and the Checkov scan. It has **not been
applied to an Azure subscription**, so names, quotas and region availability are unproven until the first
apply to test. The Ordering code also needs its Azure SQL repository before its two apps can run apart:
today the order store is in memory (see `OrderProcessing.Ordering.Functions/Program.cs`).
