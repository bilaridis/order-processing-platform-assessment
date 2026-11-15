# ADR-0001: Microservices from the start, one function per feature

- Date: 2026-11-14

## Context

The first decision of the foundation is the shape of the system: one deployable or several, and how a
feature is built inside it. The platform runs on Azure, is event driven, and must be modular,
independently scalable and extensible.

The decision is to start with microservices, for these reasons:

- **The features are independent.** Each core service handles one independent capability of the
  solution (ordering, pricing), the supporting service one shared need (documents), and each integration service one outside relationship
  (payments, inventory, carriers, communication). They change for different reasons and at different
  rates, and one should be releasable without the others.
- **They carry different demand.** Order intake is the volume and is waited on; fulfilment has the
  same volume but can queue; the rest is occasional. Separate apps let each be sized and scaled for its
  own load ([ADR-0010](0010-service-plans-by-demand.md)).
- **Failures must stay where they happen.** A carrier or payment provider being slow or down must not
  stop orders being taken. A separate app and a queue in between give that.
- **Payment handling is kept apart.** A separate payment service, with its own identity and data,
  keeps the part of the platform that is in scope for payment compliance small.

Supporting arguments that follow from these:

- **The design is event driven** ([ADR-0003](0003-event-driven-communication.md)). Services
  communicate through Service Bus, so the separation is already in the communication model.
- **Boundaries enforced by deployment cannot erode.** A separate app with separate data cannot reach
  into another service, whatever a developer does under deadline.
- **Azure Functions makes a separate app cheap to create and run**: apps share plans, and each has its
  own identity, settings and deployment slot.

What is **not** a reason: the number of teams. One team will own all the services, which is the usual
argument against microservices. It is listed under Consequences and accepted.

## Decision

### 1. Each bounded context is an independent service

| Service | Kind | Owns |
|---------|------|------|
| **Ordering** | Core | Orders, lifecycle, fulfillment process (Azure SQL) |
| **Pricing** | Core | Pricing rules and quotes |
| **Documents** | Supporting | Files and photos: upload lobby, file records, retention (Blob and Table storage) |
| **Inventory** (`inventory-integration-service`) | Integration | Inventory as a service over in-house and partner sources: routing rules, reservations (Table storage) |
| **Ecommerce** (`ecommerce-integration-service`) | Integration | Payment attempts and provider references (Table storage) |
| **Carriers** (`carrier-integration-service`) | Integration | Carrier offers for the customer to choose from, shipments and tracking (Table storage) |
| notification-integration-service (email, SMS, push) | Integration | Its own tables |
| further integrations (ERP) | Integration | Their own tables |

**Core services** hold the business rules. A **supporting service** serves the others with a shared
capability and holds no business rules: Documents, which owns files, their retention and their erasure.
**Integration services** translate
between our events and another party's API, files or callbacks, external or internal, and hold no
business rules ([ADR-0006](0006-external-integrations.md)).

Each service has its own Azure apps, its own data store that no other service can read
([ADR-0004](0004-data-ownership-and-storage.md)), its own managed identity and its own deployment
pipeline ([ADR-0011](0011-deployment.md)). A service can be released, scaled, stopped or rewritten
without touching the others.

### 2. Inside a service: one function per feature, one command, one handler

Every service is built the same way, as vertical slices:

```
Function (trigger)  →  Command or Query  →  Handler  →  Domain  →  Port  →  Adapter
```

- A **function** is the entry point of exactly one feature. In a core or integration service its
  trigger is **Service Bus** (a command or an event arrives) or a timer. HTTP triggers exist only at
  the edge: provider webhooks ([ADR-0003](0003-event-driven-communication.md)).
- **Commands are the exception to "one function per feature" at the trigger level.** A queue can have
  only one trigger, so the commands of a service arrive in one function (`OnOrderingCommand`) that
  routes on the message subject to the handler of the feature. Each feature still has its own command
  and handler. The alternative, a queue per command, multiplies queues for no gain.
- The function does three things only: turn the trigger into a **command** (or query), call the
  matching **handler**, and turn the `Result` into a response or a published event.
- The **handler** contains the use case. It knows nothing about Functions, HTTP or Service Bus.
- Clean architecture applies inside the service: domain depends on nothing, handlers depend on
  ports, Azure lives in adapters ([ADR-0002](0002-service-structure-and-boundaries.md)).

Example, the Ordering service:

| Function | Trigger | Command / query | Handler |
|----------|---------|-----------------|---------|
| `OnOrderingCommand` | Service Bus queue `ordering-commands`, subject `PlaceOrder` | `PlaceOrder` | `PlaceOrderHandler` |
| `OnOrderingCommand` | same queue, subject `CancelOrder` | `CancelOrder` | `CancelOrderHandler` |
| `OnOrderSubmitted` | Service Bus `ordering-events` | request a quote | `OrderFulfillmentProcess` |
| `OnPriceQuoted` | Service Bus `pricing-events` | store the quote, request the reservation | `OrderFulfillmentProcess` |
| `OnStockReserved` | Service Bus `inventory-events` | accept the order | `OrderFulfillmentProcess` |
| `OnPaymentAuthorized` | Service Bus `payments-events` | `ConfirmOrder` | `ConfirmOrderHandler` |
| `OnPaymentFailed` | Service Bus `payments-events` | `RejectOrder` | `RejectOrderHandler` |
| `OnShipmentDispatched` | Service Bus `shipping-events` | `MarkOrderShipped` | `MarkOrderShippedHandler` |
| `OnShipmentDelivered` | Service Bus `shipping-events` | `MarkOrderDelivered` | `MarkOrderDeliveredHandler` |
| `ExpirePendingOrders` | Timer | `RejectExpiredOrders` | `RejectExpiredOrdersHandler` |

The `ICommand` / `ICommandHandler` abstractions of the skeleton are exactly this contract, and its
handlers move into the services unchanged.

### 3. Communication

- **Events and commands over Service Bus** are the default ([ADR-0003](0003-event-driven-communication.md)).
- **There is no HTTP between services.** A request to another service is a command on its queue and
  the answer is an event, including the price quote and the stock reservation when an order is placed
  ([ADR-0005](0005-order-placement-flow.md)).
- **One public API.** Clients call a single CQRS app service, the Order API, which puts commands on
  the queue of the core service, answers "received", and answers queries from its own views
  ([ADR-0013](0013-cqrs-app-service.md)). Core services are not exposed.
- **Contracts are packages.** Each service publishes its `Contracts` project (commands, events, DTOs)
  as a versioned package. Consumers depend on the package, never on the service's code.

### 4. Hosting

Each service is deployed as Azure Functions apps on Windows App Service plans
([ADR-0009](0009-azure-hosting.md)). Apps share plans to keep cost down: isolation is at the level of
app, identity, data and deployment, which is what independence needs. The plans are drawn by demand
([ADR-0010](0010-service-plans-by-demand.md)).

## Alternatives considered

**A modular monolith first**: one deployable with enforced module boundaries, extracting services
later when measurements ask for it. Fewer moving parts and the cheapest to build and run for one team.
Not chosen, for the reasons in Context. Its warnings remain valid and are listed under
Consequences.

**One App Service web app per service, with controllers instead of functions.** Familiar and good for
HTTP-heavy services. Not chosen as the default because most features here are triggered by messages
and timers, which Functions handle natively. Acceptable for a service that is almost purely HTTP.

**A separate service for every calculation or screen** (for example a tax service, or an onboarding
service). Rejected: a service is a business capability with its own data and its own reason to
change. Tax calculation is a rule inside Pricing, behind a port if an external tax engine is ever
used; making it a service would add one more message round trip to every order. Customer onboarding is
not part of order processing: sign-up is handled by Microsoft Entra External ID, and anything beyond
that is a different system.

**One function app per feature.** Maximum isolation. Rejected: dozens of apps to deploy and monitor,
with features of one service sharing a domain model and a data store anyway. The unit of ownership
is the service; the unit of code is the feature.

**Two synchronous HTTP calls on order placement** (quote and stock reservation inside the client's
request, answer `201` or a precise error). The customer learns "out of stock" at once. Not chosen:
services talk only through Service Bus. ADR-0005 records what the checkout pays for that.

## Consequences

What we gain:

- Independent deployment, scaling and failure. A broken shipping integration does not stop orders being taken.
- Boundaries that are physically enforced.
- One repeatable shape for every feature, which makes the codebase easy to navigate and work easy to hand out.
- No future "break up the monolith" project.

What we pay, stated plainly because it will be asked:

- **More to build before the first feature works**: a host, pipeline, identity and infrastructure per
  service, contract packages, routing. About 35 person-days more than a single deployable in the
  [estimate](../estimation/01-high-level-estimate.md).
- **More to run**: more apps, more telemetry, more plans as the system grows. About two thirds more
  than a single host at launch in the [forecast](../estimation/02-azure-cost-forecast.md).
- **Distributed-system problems from day one**: partial failure, eventual consistency, duplicate and
  out-of-order messages, versioning between services. The design answers these (redelivery by the
  trigger, deterministic message ids, inbox, idempotent handlers, compensations), but the team must be disciplined about them everywhere.
- **Debugging crosses processes.** End-to-end tracing is mandatory ([ADR-0008](0008-observability.md)).
- **Local development is heavier**: several function apps and emulators instead of one process.
- **No synchronous dependency between services.** If Pricing or Inventory is down, orders are still
  received and wait in `Submitted`; they are accepted or rejected when the service returns. A customer
  who waits too long is the symptom to monitor, not an error rate.
- **One team owns seven services.** Independent deployability pays off most with several teams.
  With one team the benefit is isolation and scaling, and the cost is coordination overhead that
  should be kept low with shared templates and pipelines.
- **Wrong boundaries are expensive to move.** The analysis phase must confirm the seven contexts before
  they harden into services.

### What this means for the skeleton in this repository

The skeleton runs all services in one process. It is a **local composition** for development and
tests, and its projects map one to one onto the services:

| In the skeleton | Becomes |
|-----------------|---------|
| A module (`Contracts` + implementation projects) | A service; `Contracts` becomes its published package |
| A GraphQL resolver of the Order API module | A resolver of the customer or back-office API |
| Webhook and delivery offer endpoints | HTTP-triggered functions of the integration service |
| An `IIntegrationEventHandler<T>` method | A Service Bus-triggered function calling a command handler |
| The process manager class `OrderFulfillmentProcess` | The Ordering service's set of `On…` functions and handlers |
| A call through `IOrderingApi`, `IPricingApi`, `IInventoryApi`, `IEcommerceApi`, `ICarriersApi` | A command sent to the service's Service Bus queue |
| The boundary tests | Still run per service; boundaries are additionally enforced by deployment |
