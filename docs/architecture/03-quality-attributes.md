# 03 Quality attributes

How the architecture addresses each non-functional requirement. "In the skeleton" means it can be seen
running today; "Designed" means the decision is made and documented, and the implementation is on the
delivery team's backlog ([04](04-implementation-guide.md)).

| Attribute | How the architecture addresses it | In the skeleton | Designed |
|-----------|-----------------------------------|-----------------|----------|
| **Modularity** | Seven independent services, each with one responsibility, its own data and its own deployment. A service is visible to others only as a `Contracts` package. Inside a service, one function per feature. | Project structure; `internal` by default; [boundary tests](../../tests/OrderProcessing.ArchitectureTests/ModuleBoundaryTests.cs) fail the build on a violation. | Separate Azure apps, identities and data stores make the boundaries physical (ADR-0001). |
| **Maintainability** | Every feature has the same shape: function, command, handler. Business rules live in the aggregate. Services are small enough to understand alone. Decisions are recorded. | `Order.cs`, command handlers, `OrderFulfillmentProcess.cs`; central package versions; analyzers with warnings as errors; ADRs. | A service template so that all seven look alike; versioned contract packages. |
| **Testability** | Domain has no dependencies, so rules are tested as plain objects. Use cases depend on interfaces and are tested with hand-written fakes. External systems sit behind ports. Time is injected. | 97 tests that run in seconds with no database, no broker and no mocking library. | Integration tests against SQL Server, the storage emulator and the Service Bus emulator; contract tests per provider adapter. |
| **Scalability** | Every service is stateless and scales on its own, on queue length (the Order API on HTTP load). Reads are served by the CQRS app service from its own views and never load the core. Nothing but receiving the order is on the request path. Service Bus absorbs bursts. | Mutations answer "received"; every step driven by a message. | A plan per demand tier, each with its own autoscale rule (ADR-0010); Azure SQL scales up in place; competing consumers on Service Bus. |
| **Reliability** | A step is not finished until its message is completed: the Service Bus trigger redelivers until the handler has saved and published (no outbox, ADR-0012). Handlers send from state, event ids are deterministic. At-least-once delivery with idempotent handlers. Compensations for every step. Timeouts, retries and circuit breakers on every external call. Fail closed on stock. Optimistic concurrency on the order. | Idempotency key on order placement; idempotent transitions; compensation paths; failure matrix in [02](02-order-lifecycle.md). | Duplicate detection, retry settings and dead-letter queues on Service Bus; **dead-letter alert and resubmit tool**; inbox tables; timeouts for `Submitted` and `Pending` orders. |
| **Security** | Authentication by Microsoft Entra; the platform only validates tokens. Deny by default. Scope-based authorization plus per-order ownership. Webhooks authenticated separately. No card data ever reaches the platform: cards are entered on the provider's hosted page with 3-D Secure (PCI DSS SAQ A). Personal data mapped per store and message, kept for a set period, erased by message (ADR-0014). No stored credentials. | Fallback policy requires authentication; `orders.read` / `orders.write` / `orders.manage` scopes; "not yours" returns 404; webhook secret compared in constant time. | Managed identities; controlled SQL execution; private blobs with short-lived SAS links; WAF; network lock-down, private endpoints where required. See ADR-0007, ADR-0009, ADR-0004. |
| **Observability** | OpenTelemetry for traces, metrics and logs, exported over OTLP to any backend. One trace follows an order from HTTP request through event handlers. Errors carry a trace id back to the caller. Liveness and readiness are separate. | OTel wiring in the host; a span per handled event; problem details with `traceId`; `/health/live`, `/health/ready`. | Business metrics (orders by status, time in status); alerts on dead letters, queue age and stuck orders. See ADR-0008. |
| **Deployment** | One artefact per commit, configured by environment. Releases are safe because hosts are stateless and handlers are idempotent. Database changes are backward compatible and run as a separate, reviewed step. | CI workflow that builds, tests, checks formatting and produces the deployable package. | Zip deployment to App Service and Functions staging slots with swap; Terraform with reviewed plans and scanning; an artefact repository; migration step; environments and promotion. See ADR-0011, ADR-0009. |
| **Extensibility** | New behaviour is added, not edited in: a pricing rule is a class, a provider is an adapter behind an existing port, a new consumer subscribes to events, a new module implements `IModule`. | `IPricingRule` pipeline with two rules; `IModule`; ports for all three providers; one event per lifecycle transition. | Recipes in [04](04-implementation-guide.md). |

## Targets

The table above says how each attribute is addressed. This one says how much is enough. These are the
design targets, derived from assumption A5 and the launch volumes of the
[cost forecast](../estimation/02-azure-cost-forecast.md). The load test on staging proves them before go-live.

| What | Target | Measured as | What the design relies on |
|------|--------|-------------|---------------------------|
| Volume at launch | 5,000 orders a day, about 150,000 a month | Orders received | Launch sizing of the forecast |
| Peak order rate | 30 orders a second for 10 minutes without losing or refusing an order | Load test on staging | The queue absorbs the peak; intake scales out on queue length |
| Receiving an order (`placeOrder` answers "received") | 300 ms at the 95th percentile, 1 s at the 99th | Server duration of the mutation | The mutation only checks the shape, writes a placeholder view and sends one message |
| Accepting an order (`Submitted` to `Pending` or `Rejected`) | 2 s at the 95th percentile, 5 s at the 99th, at peak | Time between `OrderSubmitted` and `OrderPlaced` or `OrderRejected` | Two message round trips (price, stock); the inventory time budget of 2 s |
| Reading an order | 200 ms at the 95th percentile | Server duration of the `order` query | One row from Table storage, no call to the core |
| How far a view is behind | 2 s at the 95th percentile | Event time against view update time | The projector on the order-flow plan |
| Availability of receiving an order | 99.9% a month (about 43 minutes down) | Successful `placeOrder` requests against all requests | Two instances across zones; only Front Door, the customer API and Service Bus are on the request path |
| Availability of the rest of the flow | No target in percent. An outage delays orders and does not lose them | Oldest message waiting on a queue: alert at 5 minutes | Messages wait in Service Bus for up to 7 days |
| A dead-lettered message is seen by a person | Within 15 minutes in working hours | Time from dead letter to acknowledged alert | Dead-letter inspector every 5 minutes, alert to L1 support |
| Loss of one availability zone | No order lost; service continues on the other zone | Tested in staging | Zone-balanced plans. The launch database tier (S2) is not zone redundant: receiving continues, and accepting pauses until Ordering fails over to its geo-secondary. Zone redundancy comes with the vCore tier at 10 times the launch volume |
| Loss of the region: recovery time | 12 hours | Recovery exercise once before go-live | Terraform rebuilds the environment; Azure SQL geo-restore |
| Loss of the region: data loss | Up to 15 minutes of orders | Replication lag of the geo-secondary database, alert above 5 minutes | Azure SQL active geo-replication (failover group) to the paired region for the order of record, normally seconds behind; geo-redundant storage for tables and files (Microsoft states typically under 15 minutes, not guaranteed). Messages in flight on Service Bus Standard are not replicated: after a failover, Ordering re-sends the next command for every order that is not finished, from its state (ADR-0012). A `PlaceOrder` still on the queue when the region is lost is lost with it. |
| Database restore inside the region | Any point in the last 14 days | Restore exercise | Point-in-time restore of Azure SQL |
| Telemetry retention | 90 days | Workspace setting | [ADR-0014](../adr/0014-personal-data-and-payment-regulation.md) |

What these targets expose, to be honest about it:

- **99.9% is an objective, not a guarantee.** The published service levels of Front Door, App Service
  and Service Bus multiply to slightly less than 99.9% for the request path. Promising 99.9% in a
  contract needs either a lower figure or a second region ([cost forecast](../estimation/02-azure-cost-forecast.md), C8).
  The service levels must be read again from Azure's current documents before anyone signs.
- **The recovery point is bought, the recovery time is not.** Fifteen minutes of orders needs a
  geo-secondary of the Ordering database (about €65 a month at launch, the price of the primary). The
  twelve hours to recover stay, because the apps in the second region are rebuilt by Terraform, not
  kept warm. A shorter recovery time is a warm second region and roughly doubles the production cost.
- **Orders received in the last moments before a region loss can be lost** if their `PlaceOrder`
  message was still on Service Bus Standard, which does not replicate messages. Closing that gap
  needs Service Bus Premium with geo-replication (escalator C5, about €600 a month per unit).
- **Whether the launch database tier carries 30 orders a second is not known.** It is the first thing
  the load test must answer, before the plan sizes.
- Targets without an alert are wishes. Each row with a number becomes an alert rule or a dashboard
  tile in package 13 ([ADR-0008](../adr/0008-observability.md)).

## Trade-offs accepted

No design gets every attribute for free. These are the costs we chose.

| We chose | We gave up | Why |
|----------|-----------|-----|
| Microservices from the start | The simplicity of one deployable: one pipeline, in-process calls, easy local runs, easy debugging | Independent deployment, scaling and failure, and boundaries that cannot erode. More to build and run, accepted knowingly (ADR-0001). |
| Stock reservation before acceptance, fail closed | Accepting orders while the inventory system is down | An order we cannot fulfil is worse than a rejection. Orders are still received and wait. |
| No outbox | A guarantee that does not depend on how handlers are written; self-healing after a long broker outage | Every step starts from a message that the trigger redelivers; one table and one relay less per service (ADR-0012). |
| Asynchronous payment after acceptance, on the provider's hosted page | A final "paid" answer in the first response; a one-step checkout | Strong customer authentication needs the customer present for a card payment, and the amount must be final first. The provider confirms by webhook; the client reads the view (ADR-0014). |
| Pseudonymise orders, do not delete them | A literal "delete everything" on request | Tax law keeps the invoice for years; erasure removes what we are not obliged to keep (ADR-0014). |
| At-least-once delivery | Simpler handlers | Exactly-once does not exist across systems; idempotency is the honest contract. |
| Orchestration in Ordering | Fully decoupled modules | The business process is readable in one file, and failures have one owner. |
| Our own thin messaging abstractions | Features of a full bus library | A few dozen lines we understand, no licence cost, with Service Bus behind them. |
| Private data per module and per service, no joins across them | Convenient reporting queries | Reporting consumes events into its own store; services stay independent. |
| Azure SQL for the core, Table storage for integration services | One storage technology everywhere | Each is used for what it is good at; Table storage costs almost nothing but cannot be queried freely. |
| Public endpoints locked down, private endpoints only on demand | Network isolation by default | The indirect cost of going private (Service Bus Premium, a private gateway) exceeds the launch budget. |
| App Service plans shared by demand tier, not one per service; Windows plans | Resource isolation between services in the same tier; idle capacity cannot move between tiers; the lower public list price of Linux plans | Cost: several apps share one plan's instances; order placement is protected from all other work. Windows is the organisation's choice with its DevOps team, and is the cheaper option under its Azure agreement. |
| Service Bus only between services, "received, not accepted" on place order | An immediate "accepted" or "out of stock" in the response to the checkout | One way of communicating, no runtime coupling, intake survives an outage of Pricing or Inventory. The front end reads the result from the order view (ADR-0005). |
