# 04 Implementation guide

For the delivery team: what you are handed, and what to build next.

## What is real and what is a placeholder

Every piece of infrastructure has an in-memory stand-in behind the interface the real one will use,
marked `SKELETON PLACEHOLDER`. Search for that phrase to list them.

| Area | State | Next |
|------|-------|------|
| Service structure, boundaries, boundary tests | **Real** | |
| `Order` aggregate, lifecycle, history; place and cancel; the process manager from `Submitted` to `Delivered` with compensations | **Real** | |
| Contracts of every service (`IOrderingApi`, `IPricingApi`, `IInventoryApi`, `IEcommerceApi`, `ICarriersApi`) | **Real** | |
| Publishing without an outbox: deterministic event ids, publishing from the order's state | **Real** | ADR-0012 applies to every new handler |
| Both GraphQL schemas, error format, authentication, authorization, schema files checked by tests | **Real** | One web app per schema, persisted queries, cursor paging |
| Order API commands (answer "received"), views, projector, delivery offers | **Real**; views in memory | Two Table storage tables; offers view with a short time to live |
| Pricing rule pipeline | **Real** mechanism, placeholder rules | Real tax and shipping rules |
| Integration services: resolver, registry, one port per party | **Real** mechanism, fake parties | Registry and rules in Table storage; one typed `HttpClient` adapter per real party |
| Signed offer token, booking with the chosen carrier, `ShipmentFailed` | **Real** | Key Vault key pair (public key to Pricing); bind the basket |
| Notifications: policy, contact copy, delivery reports in the timeline | **Real** behaviour, fake vendors | ACS and Notification Hubs clients with managed identity; templates in Blob storage |
| Carrier webhook | **Real**, shared secret | The carrier's signature scheme |
| `InMemoryOrderRepository`, `InMemoryMessageBus`, `InMemoryInbox`, view, reservation, shipment and payment stores | Placeholder | Azure SQL (EF Core), Service Bus with duplicate detection, Table storage |
| Functions hosts: Ordering, Ecommerce, Notifications, Operations; Service Bus publisher and sender | **Compiles, never run** | Run against a Service Bus namespace; build the other hosts from them |
| Payment: `paymentMethod` and optional `paymentMethodToken` on `placeOrder` | Contract real; background authorization with a fake | Hosted page with 3-D Secure, `PaymentActionRequired` (ADR-0014); payment webhook |
| Disputes (`openDispute`, `resolveDispute`, `OrderDisputeOpened`, `OrderDisputeResolved`) | Designed; `ShipmentFailed` is logged today | [02](02-order-lifecycle.md#disputes) |
| Dead-letter alert, order timeouts | Designed | Alert rule and runbook; a timer function in Ordering |
| Personal data: redaction, retention purges, erasure workflow | Designed | One erasure handler and purge timer per service ([07](07-data-protection-and-compliance.md)) |
| Documents service | Designed | [ADR-0004](../adr/0004-data-ownership-and-storage.md) |
| Local host (`OrderProcessing.Host`) | **Runs** | Stays as the way to run everything on one machine |

## The Functions hosts

The same modules run in two hosts; nothing in a module knows which.

| | Local host | Functions host of a service |
|---|---|---|
| Trigger | Order API endpoints, in-memory bus | Service Bus triggers only |
| Calls to other services | In process through `Contracts`; answers as events on the in-memory bus | The same `Contracts` as queue senders; answers on a subscription |
| Events | In-memory bus and inbox | `ServiceBusEventPublisher`; one function per subscription, inbox |

- **No HTTP trigger in a core service.** Ordering has one function on its command queue and one per
  event on its own subscription.
- **A command queue has one trigger**, so the commands of a service share one function
  (`OnOrderingCommand`, `OnPaymentCommand`) and are routed by message subject to their own handlers.
- **One subscription per event**, filtered on the subject, so each step can be retried and
  dead-lettered on its own.
- One build can run as several apps ([ADR-0010](../adr/0010-service-plans-by-demand.md)) once the
  order store and inbox are durable; until then Ordering runs as one app.

## Suggested build order

Each step leaves the system working. Steps 1 to 3 remove the placeholders that matter for correctness
and set the service template; the rest can run in parallel.

1. **Persistence for Ordering.** EF Core on Azure SQL, schema `ordering`; `rowversion`; unique index on
   `(customer_id, idempotency_key)`; reviewed SQL migrations; database users per managed identity.
2. **Service Bus and the safety net.** Queues, topics and subscriptions with duplicate detection; inbox
   in the same `SaveChanges` as the order; dead-letter alert, resubmit tool and runbook; a
   failure-injection test per handler (stop after the save, deliver again, the event is published).
3. **Service template.** Functions host with one function per feature, inbox, telemetry, health and
   managed identity. Ordering and Ecommerce first, one order end to end through Service Bus, then the
   rest; Front Door routes; the order timeout timer.
4. **Inventory adapter** with contract tests against the party's sandbox.
5. **Payment adapter**, hosted page and signed webhook.
6. **Carrier adapter**, signed webhook, labels and photos to Blob storage.
7. **Pricing**: real price source, tax and shipping rules, rounding per currency.
8. **Documents**: lobby, scan, file records, `file(token)` in the Order API.
9. **Operational hardening**: readiness per dependency, rate limits, business metrics, dashboards, alerts.
10. **Infrastructure**: Terraform modules for the remaining services from `ordering-service`, release
    pipelines, budgets and cost alerts.

## Recipes

**Add a feature to a service.** Add a command and its handler in the application layer (see
`CancelOrder.cs`); put rules in the aggregate and return a `Result`. Add one function that builds the
command and calls the handler, with no logic. The handler publishes from the state it finds, with a
deterministic event id. Test it, including "run it twice". If clients need it, add a mutation to the
Order API and its schema file.

**Add a pricing rule.** Implement `IPricingRule`, choose its `Order` (before tax if it is taxed), and
register it in `PricingModule`. Nothing else changes.

**Add a party (provider, source, carrier).** Write an adapter for the port, register it, enable it in
the registry (`Inventory:Sources`, `Carriers:Enabled`). The contracts and Ordering do not change.

**React to an order event.** Reference `OrderProcessing.Ordering.Contracts`, implement
`IIntegrationEventHandler<T>`, register it, make it idempotent.

**Add a service.** From the template: `Contracts`, implementation, Functions host; its own store,
identity, pipeline and Terraform module; its topic, queue and subscriptions. The boundary tests apply
by naming convention.

## Delivery tooling

| Tool | Used for | Rule |
|------|----------|------|
| **GitHub** | Source, reviews, Actions, OIDC to Azure, packages | Nothing reaches `main` without a reviewed pull request and green checks |
| **Jira Service Management** | Incidents, service requests, production change approvals | Azure Monitor alerts open incidents; a release links to its change |
| **Jira boards or GitHub Issues** | The backlog | The team picks one at the start; every pull request names its ticket |
| **The repository** | All documentation | Changed in the same pull request as the code it describes |

Runbooks go in `docs/operations`. Tool licences are not in the estimate or the forecast.

## Conventions

- **Errors.** Business outcomes are `Result` values with a stable `code`; exceptions mean a bug or broken infrastructure.
- **Time.** Inject `TimeProvider`.
- **Identifiers.** Version 7 GUIDs, generated by the platform.
- **Money.** `decimal` with an ISO 4217 currency; one rounding policy, in Pricing.
- **Visibility.** `internal` unless in `Contracts`, `Domain`, or the module entry point.
- **Tests.** Name the behaviour; hand-written fakes over mocking libraries.
- **Decisions.** A change that contradicts an ADR updates that ADR in the same pull request.
