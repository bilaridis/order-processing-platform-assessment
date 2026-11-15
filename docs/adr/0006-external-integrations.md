# ADR-0006: Four integration services, each one contract in front of many parties

- Date: 2026-11-14

## Context

Inventory, payment and shipping are systems we do not control. Their models, error codes, latency and
uptime are theirs. There is rarely just one of each:

- **Inventory** may be in-house, at one or more partners, or both. The core should ask "can this be
  supplied" without knowing where the stock is.
- **Carriers** are several, and which one is best is the customer's judgement, not ours: cheaper,
  faster, a locker near home.
- **Payment** providers differ per payment method.
- **Customer notifications** go over email, SMS and push, and each channel has its own vendors and prices.

None of that should reach the Order aggregate.

## Decision

**One integration service per capability, each an anti-corruption layer**:

| Service | Capability | What it hides |
|---------|-----------|---------------|
| `ecommerce-integration-service` | Payments | Which payment provider handles which payment method |
| `inventory-integration-service` | Inventory as a service | Which inventory source, in-house or partner, is checked and holds the stock |
| `carrier-integration-service` | Shipping (module `Carriers`) | How each carrier quotes, books and tracks; the customer sees one list of offers |
| `notification-integration-service` | Email, SMS and push to the customer | Which service sends each channel: Azure Communication Services for email and SMS, Azure Notification Hubs for push |

The same applies to internal parties (ERP, warehouse).

**The notification service is a pure consumer.** It subscribes to order, payment and shipping events,
decides from a policy table which event goes to which channel, and sends. No service asks it for
anything and nothing waits for it, so it can be down without affecting an order. It keeps its own
copy of the customer's contact details from `OrderSubmitted`; the core does not call it and it does
not call the core.

**One contract, a resolver, many adapters.** Each service exposes a contract in our language that is
agnostic of the party behind it (`IInventoryApi.ReserveAsync`). Inside, a **resolver** chooses the
party from a registry and rules kept in the service's own tables, and calls it through one port
(`IInventorySource`, `ICarrier`, `IPaymentProvider`) with an adapter per party. Provider DTOs, status
codes and exceptions never leave the adapter. Parties are added, enabled and prioritised by
configuration.

- *Inventory:* validate the items, resolve candidate sources by routing rules, reserve all or nothing
  inside one time budget, release internally on partial failure.
- *Carriers:* the service owns **no selection policy**. It collects the offers of all carriers
  (service, price, delivery date) and returns the list; the customer chooses, as on a comparison site.
  The order carries the chosen offer as a signed token, and the service books exactly that.
- *Ecommerce:* resolve by payment method, then by rule where providers overlap.
- *Notifications:* one port per channel (`IEmailSender`, `ISmsSender`, `IPushSender`) and one vendor
  per channel from the registry. One message per order, event and channel, guarded by the inbox.

The detail is in [06 Integration services](../architecture/06-integration-services.md).

**Outbound calls** use typed `HttpClient`s with a resilience pipeline
(`Microsoft.Extensions.Http.Resilience`), configured per provider:

| Policy | Default | Note |
|--------|---------|------|
| Timeout per attempt | 2 s for inventory (it is on the request path); longer for background calls | |
| Retry | 3 attempts, exponential backoff with jitter, on transient failures only | Only because every call is idempotent |
| Circuit breaker | Opens on sustained failure rate | Fail fast instead of queueing requests behind a dead provider |
| Bulkhead | Bounded concurrency per provider | One slow provider cannot exhaust the app |

**Every mutating call carries an idempotency key derived from the order id** (`order-{id}`), so retries,
redeliveries and crashes cannot reserve twice, charge twice or ship twice.

**Inbound calls (webhooks)** land in the owning service as an HTTP-triggered function, are authenticated by the provider's signature
scheme, acknowledged quickly, and translated into an integration event. Webhooks are treated as
at-least-once and possibly out of order; the Order aggregate's transition rules absorb both.

**Failures are translated into our outcomes**: a business refusal (declined card, short stock) becomes a
normal result or event; a technical failure becomes `Unavailable` on the request path, or a retry on the
background path.

**Testing.** Other services test against fakes of the contract. Each adapter gets contract tests against
the provider's sandbox or recorded responses.

## Alternatives considered

**Call provider SDKs directly from Ordering.** Fewer types. Rejected: provider concepts leak into the
domain, and replacing a provider becomes a change to order processing.

**A separate service per party** (one per carrier, one per inventory source). Maximum isolation.
Rejected: the choice between parties is the valuable logic and needs one place to live; the core would
have to know the parties again.

**A platform-owned carrier selection policy** (the platform picks the cheapest or the fastest carrier
for a delivery option). Lets the business steer cost. Not chosen: the platform should
not own that judgement. The carriers make the offers and the customer decides. A policy could be added
later as a default pre-selection without changing the contracts.

**SendGrid for email and Twilio for SMS** instead of Azure Communication Services.
The more established vendors, with richer delivery analytics and wider SMS coverage. Not chosen: two more vendor contracts and invoices, API keys to keep in Key Vault, and resources outside
Terraform's Azure provider. Twilio remains the fallback for SMS if ACS does not cover Greek numbers
with a sender ID, behind the same `ISmsSender` port.

**Notifications sent by Ordering** (Ordering calls the email vendor when the order changes). Fewer
parts. Rejected: Ordering would depend on a vendor and on message wording, and a slow email vendor
would slow the order flow.

**Selection logic in Ordering.** Rejected: Ordering would change whenever a carrier or a warehouse is
added.

**One generic "Integrations" service.** Rejected: three unrelated reasons to change in one place, and
payment code (the most sensitive) would share a boundary with everything else.

**Retries without idempotency keys.** Rejected outright: a retried payment authorization without a key
is a double charge.

## Consequences

- Swapping or adding a provider is a new adapter and a registry row; contracts and Ordering are untouched.
- The inventory service holds real logic (routing, allocation). It is integration logic, not order
  logic, but it needs its own tests and its own observability: every choice is recorded with its reason.
- Delivery offers are requested at every checkout, while the customer waits, from several carriers.
  One time budget and parallel calls; a slow carrier is left out of the list.
- The delivery fee is the carrier's price, so it is not known before the offers are fetched, and it
  can differ between two visits. Offers expire and are signed so they cannot be altered.
- Our shipping cost is whatever customers choose. The business steers it only by which carriers are
  enabled and by Pricing rules on top.
- If the chosen carrier refuses a paid order's shipment, there is no automatic substitute. The order
  goes into a **dispute** that the company settles with the customer, with or without a refund
  ([02](../architecture/02-order-lifecycle.md#disputes)).
- Calling several inventory sources inside the order request multiplies the ways it can be slow. One
  overall time budget, parallel calls and fail closed keep order placement predictable.
- Supplying one order from several sources implies several shipments, which is outside the launch scope.
  The split strategy is designed and switched off.
- A translation layer has to be written and maintained per provider.
- Ecommerce is isolated in its own service, with its own identity and data, which keeps PCI scope small.
- The platform inherits each provider's idempotency guarantees. Where a provider has none, the service
  must keep its own record of requests in its tables ([ADR-0004](0004-data-ownership-and-storage.md)).
- The adapters in the skeleton are fakes. The notification service is designed, not built.
- **SMS is the most expensive message the platform sends.** At launch volume, one SMS per order costs
  more than the whole Azure bill, so the number of SMS per order is a business decision recorded in the
  notification policy ([06](../architecture/06-integration-services.md#what-it-costs-to-run)).
- **Contact details become order data.** The checkout must capture email and mobile number, they travel
  in `OrderSubmitted`, and the notification service holds a copy under the same retention rules.
- Notifications stay inside Azure: managed identity, no API keys, delivery reports routed by Event Grid into a Service Bus queue (so the only trigger is still Service Bus),
  everything in Terraform. The risk is SMS coverage for Greece, to be confirmed before the build.
