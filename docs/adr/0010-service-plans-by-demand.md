# ADR-0010: App Service plans split by demand

- Date: 2026-11-14

## Context

Function apps share App Service plans ([ADR-0009](0009-azure-hosting.md)). That leaves the question of
*how* the plans are drawn, and the obvious answers are both poor: one plan for everything lets a busy feature starve a quiet one, and
one plan per service pays for seven sets of mostly idle instances
([cost forecast](../estimation/02-azure-cost-forecast.md), about €1,765 instead of €675 a month).

**The plans are drawn by demand, not by service.** The features of this platform
do not carry the same load:

- **Placing an order** is the volume: thousands of orders, and every one of them goes through it.
- **Retrieving an existing order** is expected to be the exceptional case.
- **Cancelling an order** is exceptional.

Features with the same demand profile should share hardware; features with different profiles should not.

One technical fact shapes the decision: **a plan is assigned to a function app, not to a function.**
Two features of the same service can only run on different plans if they are deployed as different
function apps.

## Decision

**1. Every feature is classified by its demand profile, and there is one App Service plan per profile.**

| Demand tier | What drives the load | Function apps on it | How the plan is sized and scaled |
|-------------|----------------------|---------------------|----------------------------------|
| **Order intake** (hot path) | Every order, while the customer waits for "accepted" or "rejected" | Customer API (the presentation app of the web shop and mobile app, [ADR-0013](0013-cqrs-app-service.md)), Ordering *intake* (place order), Pricing (quote), Inventory (reserve), Carrier *offers* (delivery offers at checkout) | For the **peak** order rate. At least two instances. The customer API scales on CPU and HTTP queue; the function apps scale on the length and age of their command queues. |
| **Order flow** (same volume, nobody waiting) | Every order, but nobody is waiting | Ordering *flow* (the process functions after acceptance), Ecommerce, Carrier *shipments* (booking, tracking), Notifications (every status change is a message to the customer), Order API *projector* (order views) | For the **average** order rate. Service Bus absorbs the peaks. Scale on queue length. |
| **On demand** (exceptional) | A fraction of orders, by people or back-office tools | Back-office API (staff, [ADR-0013](0013-cqrs-app-service.md)), Documents, ERP adapters, the operations tool (dead-letter inspector and resubmit) | Small and steady. Scale only if measurements say so. |

**2. A service may be deployed as more than one function app when its features sit in different tiers.**
Ordering becomes two function apps: `ordering-intake` (its command queue: place order and cancel order)
and `ordering-flow` (the fourteen event functions of the order flow). Cancelling is exceptional, but it
arrives on the same command queue as placing an order and costs almost nothing, so it stays in the
intake app rather than getting an app of its own.
The carrier service becomes two: offers at checkout, and booking with tracking.
It is still **one service**: one codebase, one team, one database schema, one contracts package, one
pipeline that builds once and deploys both apps together. Because every feature is already its own
function with its own command and handler ([ADR-0001](0001-microservices-and-function-per-feature.md)), choosing which app a function is registered in is
a few lines of host configuration, not a redesign.

**3. At launch, two plans.** *Order intake* gets its own plan from the first day, so that nothing can
take capacity from the path the customer is waiting on. *Order flow* and *on demand* share the second
plan while both are small, as two separate sets of apps that can be parted without a code change.
The third plan appears when either of them needs to scale differently from the other.

**4. Tier membership is data, reviewed against measurements.** The plan of each function app is a
parameter in the infrastructure templates. Moving an app to another plan is a deployment. The
classification is reviewed after the load test and then with the monthly cost review, using requests,
CPU and memory per app from Application Insights.

## The assumption it rests on

The classification rests on **retrieval of orders from the core being exceptional**. The CQRS app service ([ADR-0013](0013-cqrs-app-service.md)) is what makes
it hold: order pages, lists and tracking are answered from its views in Table storage, so however
often clients read, the reads do not reach Ordering or Azure SQL. How often clients read sizes only the
customer API, which scales on HTTP load. If reads become heavy,
the query side of the app service is deployed as its own app on its own plan; the cost of that is
priced in the forecast (escalator C2c).

## Alternatives considered

**A third Ordering app for cancellation** (`ordering-operations` on the on-demand plan). Matches
the tiers exactly. Not chosen: cancel shares the command queue with place order, so a separate app needs
a separate queue as well, and one more slot, set of settings and deploy target for a command that is
rare and cheap. Cancellation moves to its own queue and app if it ever loads the intake plan.

**One shared plan for everything.** Cheapest to reason about.
Rejected because a burst of fulfilment or back-office work would compete with order placement for the
same CPU and memory, which is the one thing that must not happen.

**A plan per service.** Full hardware isolation. Rejected as a rule: it multiplies the compute bill by
the number of services and still does not separate a hot feature from a quiet one *inside* Ordering.

**Plans by kind of service** (core plan, integration plan). Reflects the design, not the
load: Inventory is an integration service but sits on the hot path, and retrieving an order is a core
feature that is rarely used.

**Consumption or Flex Consumption for the exceptional tier.** Pay per execution, scale to zero: a
natural fit for rare work. Kept as the next option for the *on demand* tier. Not taken now because
[ADR-0009](0009-azure-hosting.md) chose dedicated Windows plans, and a first request after idle would be slow for the person
waiting on it.

## Consequences

- The capacity the customer is waiting on is protected from everything else from day one.
- Compute is paid for where the demand is. The launch bill does not change: the same four vCPUs are
  bought as two plans of two small instances instead of one plan of two larger ones.
- Each tier scales on the signal that fits it: HTTP load, queue length, or not at all.
- **More to deploy**: Ordering is two function apps and the carrier service two, and the Order API adds two web apps (customer and back office) and a projector. With Notifications and the operations tool that is thirteen apps in total, each with
  its staging slot, settings and identity. This is template and pipeline work, a few days, and is
  covered by the contingency in the [high-level estimate](../estimation/01-high-level-estimate.md).
- **Capacity is fragmented.** Idle instances in one tier cannot help another. With very small plans
  this can cost more than sharing; it is why the launch position is two plans and not three.
- The two Ordering apps share one schema and must be deployed from the same build. A migration is
  still one reviewed step for the service ([ADR-0011](0011-deployment.md)).
- Private inbound access, if ever required, is per app: thirteen endpoints instead of seven
  ([cost forecast](../estimation/02-azure-cost-forecast.md#the-cost-of-going-private)).
- The classification can be wrong. It is cheap to change and there is a named review point for it.
