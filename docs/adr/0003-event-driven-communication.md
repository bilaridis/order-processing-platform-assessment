# ADR-0003: Event-driven communication over Azure Service Bus, no HTTP between services

- Date: 2026-11-14

## Context

Services need to ask each other for things (price this basket, reserve this stock, authorize this
payment) and to learn about things that happened (payment authorized, parcel dispatched). Internal
parties (ERP, warehouse, notifications) need to follow orders without calling the core. The services
run in separate apps ([ADR-0001](0001-microservices-and-function-per-feature.md)), so every message
crosses a process boundary. No state change may be lost: a lost `OrderPlaced` is an order nobody fulfils.

## Decision

**1. Azure Service Bus (Standard tier) carries everything that crosses a service boundary.**

| Element | Used for | Example |
|---------|----------|---------|
| **Topic per publisher** | Events: facts that any number of parties may care about | `ordering-events`, `payments-events`, `shipping-events` |
| **Subscription per consumer**, with filters | Each party receives only what it asked for, at its own pace | `ordering-events / carrier-service`, `ordering-events / erp` |
| **Queue per service** | Commands: a request addressed to one service | `payments-commands` |

**2. No HTTP between services.** Every function of a core or integration service is started by a
**Service Bus trigger**. A service is asked for something through its command queue and tells what
happened on its topic. HTTP exists only at the edge: front-end apps to the Order API
([ADR-0013](0013-cqrs-app-service.md)) and webhooks from providers.

**3. Every request is a command plus an event.** Ordering sends `RequestQuote` to `pricing-commands`
and the outcome arrives as `PriceQuoted` or `PriceQuoteRejected`; the same shape for stock
(`ReserveStock`), payment (`AuthorizePayment`) and shipment (`RequestShipment`). Nobody waits on
someone else's latency, and an outage queues work instead of failing it.

| Queue | Commands (message subject) | Answered on |
|-------|----------------------------|-------------|
| `ordering-commands` | `PlaceOrder`, `CancelOrder` | `ordering-events` |
| `pricing-commands` | `RequestQuote` | `pricing-events` |
| `inventory-commands` | `ReserveStock`, `ReleaseStock` | `inventory-events` |
| `payments-commands` | `AuthorizePayment`, `ReversePayment` | `payments-events` |
| `shipping-commands` | `RequestShipment`, `CancelShipment` | `shipping-events` |

**4. Facts are published directly, without an outbox.** The trigger redelivers the incoming message
until the handler has saved and published, handlers send from the state they find, and event ids are
deterministic so a repeat is recognised. Delivery is **at least once**, so every handler is
idempotent. The reasoning, the rules and the cost are in [ADR-0012](0012-reliable-messaging-without-an-outbox.md).

**5. Contract interfaces remain the abstraction.** Callers depend on `IPricingApi`, `IPaymentsApi` and
so on from the `Contracts` packages. Every method is a request (`RequestQuoteAsync`,
`RequestReservationAsync`); behind the interface is a Service Bus sender in Azure and a direct call in
the local host. The calling code does not know which.

**6. Domain events stay inside a service.** The aggregate raises `OrderStatusChanged`; one place
(`OrderStore`) translates it into the public integration event. Internal models can change without
breaking consumers.

**7. Messaging rules.**

- **Message id = event id, derived from the order and the fact**, with duplicate detection enabled,
  and idempotent handlers on top (an inbox per service for handlers that are not naturally idempotent).
- **A queue has one trigger**, so the commands of a service share one function that routes on the
  message subject to the handler of the feature. Events get one function each, which means **one
  subscription per consumer and event type**, with a filter on the subject. Ordering alone has more
  than ten. They are declared in Terraform, not by hand.
- **Sessions keyed by order id** where order of processing matters for one order.
- **Failures:** automatic retries, then the dead-letter queue. Dead-letter count is an alert.
- **Small messages**: identifiers and facts, never documents.
- **Internal and external parties never share our internal topics directly.** Each gets its own
  subscription, or an integration function that translates to its format.

**8. No mediator library, no service bus library.** Use cases are plain `ICommandHandler<,>` /
`IQueryHandler<,>` interfaces called by the function that owns the feature. The event abstractions
(`IIntegrationEvent`, `IIntegrationEventHandler<T>`, `IEventPublisher`) are our own and total a few dozen
lines; the Azure Functions Service Bus trigger and the Azure SDK do the transport.

## Alternatives considered

**HTTP between services where the caller needs the answer at once**, for example the price quote
and the stock reservation on order placement. The customer gets a final
answer in one response. Not chosen: it couples the availability of order intake to Pricing and
Inventory and needs timeouts and circuit breakers; the cost of dropping it is in
[ADR-0005](0005-order-placement-flow.md).

**Direct HTTP calls between services for everything.** Simplest to reason about. Rejected: it couples
availability, Ordering would have to know every consumer of order changes, and a crash between
"save order" and "call payments" would lose the order.

**A transactional outbox in every publishing service.** Not chosen, see [ADR-0012](0012-reliable-messaging-without-an-outbox.md).

**Azure Storage queues.** Cheaper and simpler. Rejected: no topics and subscriptions, no sessions,
no duplicate detection, weaker dead-lettering.

**Azure Event Grid.** Good for reacting to Azure resource events and for push delivery over HTTP.
Rejected as the backbone: we need queues with competing consumers and sessions for business messages.
It can complement Service Bus later.

**Azure Event Hubs.** Built for high-volume streams and replay. Rejected: order processing is
business messaging, where per-message settlement and dead-lettering matter more than throughput.

**MediatR for use-case dispatch.** Familiar to most .NET teams. Rejected: the indirection hides which
handler a function calls, pipeline behaviours can be replaced by ordinary decorators, and recent
versions are commercially licensed. Direct injection is navigable with "go to definition".

**MassTransit or a similar bus library.** Brings a tested outbox, retries and sagas. Rejected for the
same licensing reason and because it shapes the whole codebase around a library. Our abstractions are
thin enough to adopt one later.

## Consequences

- One way of talking between services, and one thing to secure, monitor and retry: the broker.
- No state change is lost as long as handlers follow the rules of ADR-0012 and dead-lettered messages are acted on.
- A provider outage queues work instead of failing it.
- New consumers are added by creating a subscription, without touching the publisher.
- Consumers see events slightly after the change (eventual consistency between services).
- Handlers must be idempotent, which is a discipline to review for.
- Events are public contracts: versioned additively, documented in
  [integration-events.md](../api/integration-events.md).
- The broker is one more thing that must be available, monitored and paid for. On the Standard tier
  the cost is small.
- **Standard to Premium is a large cost step** (roughly 65 times the base price). It is forced by
  private endpoints, by messages above 256 KB, or by a need for dedicated throughput.
- **Nothing between services is immediate.** Any feature that needs an answer in the same request
  (the checkout is the first) must be designed around "accepted, result follows".
- **Many subscriptions and filters to manage**, and more message operations to pay for. On the
  Standard tier the price per operation is small; the count is in the cost forecast.
- A request and its answer are two messages in two places, so following one order needs the trace.
- End-to-end tracing must be carried in message properties ([ADR-0008](0008-observability.md)).
- Local development needs the Service Bus emulator or a shared development namespace. The skeleton
  uses an in-memory bus as a stand-in.
