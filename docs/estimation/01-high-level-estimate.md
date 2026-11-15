# 01 High-level estimate (HLE)

## Scope of the estimate

**Included:** taking the skeleton in this repository to a production launch on Azure as seven independent
services ([ADR-0001](../adr/0001-microservices-and-function-per-feature.md): Functions apps, Service Bus, Azure SQL,
Table and Blob storage). That is the
[suggested build order](../architecture/04-implementation-guide.md#suggested-build-order) plus analysis,
Azure infrastructure, testing and go-live.

**Not included:** the client applications (web shop, mobile, back office UI); the external inventory,
payment and shipping systems themselves; data migration from a legacy system; returns, partial shipments
and order amendments (assumption A7); operation after hypercare; licences for delivery tooling
(GitHub, Jira). Each of these is listed below as a
possible escalation if it turns out to be in scope.

## Method

- Each work package has a **three-point estimate** in person-days: optimistic (O), most likely (M) and
  pessimistic (P). The expected value is the PERT mean, (O + 4M + P) / 6.
- **15% is added** on top for technical leadership, code review, ceremonies and coordination.
- The **commit figure** adds one standard deviation, treating the risks as correlated (when one
  integration is harder than expected, the others usually are too). That is roughly an 80 to 85% confidence level.
- A person-day is one productive engineering day. Holidays and sickness are a calendar matter, not an effort matter.

## Team assumed

| Role | Allocation |
|------|-----------|
| Technical lead / architect | 50% |
| Backend engineers (.NET) | 3 full time |
| QA / test automation | 50% |
| DevOps / Azure engineer | 50% |

About 4.5 people, of which roughly 4 produce work-package output.

## Work packages

| # | Work package | O | M | P | Expected | Main uncertainty |
|---|--------------|--:|--:|--:|---------:|------------------|
| 0 | **Analysis and discovery** | 16 | 27 | 43 | 27.8 | See the breakdown below |
| 1 | Persistence for Ordering on Azure SQL (EF Core, reviewed migrations, least-privilege users, integration tests) | 8 | 12 | 18 | 12.3 | Low |
| 2 | Reliable messaging without an outbox ([ADR-0012](../adr/0012-reliable-messaging-without-an-outbox.md)): duplicate detection and retry settings, inbox with the order's transaction, dead-letter alert, resubmit tool and runbook, a "run it twice" test per handler | 3 | 5 | 9 | 5.3 | Whether every handler really sends from state; found only by failure tests |
| 3 | Service template and hosts: a Functions app per service with one function per feature, Service Bus topology, inbox in Table storage, local development setup with emulators | 12 | 18 | 30 | 19.0 | First end-to-end slice across services; emulators |
| 3b | Contracts between services: versioned contract packages, command and event contracts for every service (no HTTP clients), contract tests, Front Door routing for the Order API and webhooks | 5 | 8 | 15 | 8.7 | Versioning discipline; number of subscriptions and filters |
| 4 | Order timeouts (`Submitted`, `Pending`) and scheduled work | 3 | 5 | 8 | 5.2 | Low |
| 5 | Inventory as a service: source registry, routing rules, all-or-nothing reservation, one real source adapter, contract tests | 12 | 18 | 30 | 19.0 | Number and quality of sources; whether a source can reserve |
| 6 | Payments integration, webhook, payment records in Table storage | 12 | 20 | 35 | 21.2 | Authorize/capture, 3-D Secure, refunds, provider certification |
| 7 | Carrier integration: carrier registry, delivery offers from all carriers with signed offer tokens, booking of the chosen offer, one real carrier adapter, signed webhook, shipment records, labels and photos to Blob storage | 12 | 18 | 30 | 19.0 | Number of carriers, whether each can quote by API, tracking model |
| 7b | Notification integration service: email and SMS (Azure Communication Services) and push (Notification Hubs) adapters, notification policy table, templates per language, contact copy and preferences, delivery reports from Event Grid into a Service Bus queue, retention purge | 8 | 12 | 20 | 12.7 | Which events and channels the business wants; mobile app at launch; SMS coverage and sender ID for Greece |
| 8 | Pricing: price source, real tax and shipping rules | 8 | 15 | 30 | 16.3 | Tax complexity, number of countries |
| 9 | Documents module: upload lobby with registration, malware scan and copy to final containers, file records and file tokens, SAS links signed by the Order API, lifecycle rules | 5 | 8 | 14 | 8.5 | Malware scanning and retention requirements |
| 10 | API completion (listing with cursor paging, validation, API tests, persisted queries for production) | 5 | 8 | 12 | 8.2 | Low |
| 10b | Order API, the CQRS app service: customer and back-office web apps from one codebase (two identity providers, staff roles, Front Door routes), a GraphQL schema for each with depth and cost limits and no introspection in production, schema files as the contract with a drift test, mutations queued and answered "received", order id from the idempotency key, placeholder view, order views in Table storage, projector functions, rebuild job | 12 | 20 | 34 | 21.0 | View shapes the clients need; back-office search; the team's experience with GraphQL |
| 11b | Data protection and payment compliance ([ADR-0014](../adr/0014-personal-data-and-payment-regulation.md)): payment step with 3-D Secure on the provider's hosted page (session, `PaymentActionRequired`, view action, "complete your payment" link, abandonment), redaction of personal data in telemetry, TTL and dead-letter purge, retention purges per store, erasure workflow across services with completion tracking, data export, data map and processor register | 10 | 15 | 25 | 15.8 | Provider's session and webhook model; retention periods and roles confirmed by legal |
| 11 | Security hardening (Entra integration, managed identities, rate limiting, audit, threat model, fixes after penetration test) | 6 | 10 | 18 | 10.7 | Findings of the penetration test |
| 12 | Observability (business metrics, dashboards, alerts, tracing across seven services and Service Bus) | 6 | 10 | 15 | 10.2 | Low |
| 13 | Azure infrastructure as code for three environments (Terraform: modules per service, remote state, plan review and scanning in the pipeline), artefact repository | 12 | 18 | 30 | 19.0 | Corporate landing-zone and network rules; private endpoints if required |
| 14 | Delivery pipelines: a shared template and a pipeline per service (packages, slots and swap, migration step, promotion, gates) | 8 | 12 | 20 | 12.7 | Number of services; approval gates |
| 15 | System testing across services: end-to-end, load, resilience and failure injection, UAT support | 12 | 18 | 30 | 19.0 | Performance targets not yet agreed |
| 16 | Go-live readiness: runbooks, restore drill, cost alerts, hypercare | 5 | 8 | 15 | 8.7 | Operational maturity of the organisation |
| | **Sum of work packages** | **180** | **285** | **481** | **300** | |
| | Leadership and coordination, 15% | 27 | 43 | 72 | 45 | |
| | **Total** | **207** | **328** | **553** | **345** | |

## Result

| Scenario | Person-days | At €450 / day | Meaning |
|----------|------------:|--------------:|---------|
| Optimistic | 207 | about €93k | Everything goes as planned. Do not plan on it. |
| **Expected** | **345** | **about €155k** | The most probable outcome. Use for planning. |
| **Commit** | **404** | **about €182k** | Expected plus contingency. Use for the budget. |
| Pessimistic | 553 | about €249k | Most packages land at their upper bound. |

The contingency between expected and commit is 59 person-days, about 17%. It is not padding inside the
tasks; it is held by the lead and released against named risks.

**What the microservice foundation costs.** Compared with a single-host design, the expected total is
about 35 person-days higher (package 3 grows, package 3b is new, and infrastructure, pipelines,
observability and system testing each grow with the number of services). That is the price of
independent deployment, scaling and failure from the first release.

**What the integration services cost.** Putting a resolver in front of inventory sources and carriers
([06 Integration services](../architecture/06-integration-services.md)) adds about 17 person-days to the
expected total. The estimate includes **one** real party per capability; each further inventory source,
carrier or payment provider is priced by E9.

**What the CQRS app service costs.** The Order API with its views and projector
([ADR-0013](../adr/0013-cqrs-app-service.md)) is package 10b: about 24 person-days in the expected
total, coordination included. Splitting it into a customer API and a back-office API adds about 2 of
those (a second host, a second identity provider and the staff roles), and GraphQL instead of REST about 5
(two schemas, query limits, the persisted-query setup for production and the front-end teams' tooling).

**What Service Bus only and no outbox change.** Dropping the outbox and the two HTTP clients removes
about 9 person-days from the expected total, coordination included (package 2 shrinks from 12.7 to
5.3, package 3b from 10.7 to 8.7, package 4 grows by 1.2 for the second timeout). The saving is real
but small, and it is bought with two things that are **not** in this estimate: the front-end work to
show "we are checking your order" and read the result ([ADR-0005](../adr/0005-order-placement-flow.md)),
and the operational duty to act on dead-lettered messages. E15 and E16 below price the risks.

**What the notification service costs.** Package 7b: about 15 person-days in the expected total,
coordination included. Its running cost is dominated by SMS; see the
[cost forecast](02-azure-cost-forecast.md#notifications-email-sms-and-push).

**What compliance costs.** GDPR and the payment step that strong customer authentication requires
([ADR-0014](../adr/0014-personal-data-and-payment-regulation.md)) are package 11b plus two more days of
analysis: about 20 person-days in the expected total, coordination included. 3-D Secure moved from the
escalation list (E2) into the base, because it is not optional for card payments in the EU. Issuing
invoices to myDATA is not in the base (E17).

**Calendar.** Analysis runs for 3 to 4 weeks with two people while the rest of the team starts packages
1 to 3, which do not depend on its outcome. Delivery then takes about 18 to 20 weeks. That gives a launch
after roughly **5 months**, and **6 months** as the date to commit to.

## The analysis phase and its cost

The analysis phase is estimated and can be approved separately. Its job is to turn the
[assumptions](../architecture/01-overview.md#assumptions) into facts and to shrink the range above.

| Activity | Output | Person-days (O / M / P) |
|----------|--------|------------------------:|
| Business process workshops: order lifecycle, cancellation and payment rules, edge cases | Confirmed state machine and rules; scope decisions on returns and partial shipments | 3 / 5 / 8 |
| Provider analysis: inventory, payment, shipping APIs, sandboxes, limits, idempotency, webhooks | Integration specifications; list of gaps and risks per provider | 4 / 7 / 12 |
| Pricing and tax analysis | Rule catalogue, countries, rounding, build or buy for tax | 2 / 4 / 7 |
| Non-functional targets: volumes, peaks, response times, availability, recovery objectives | Agreed, measurable targets; sizing input for Azure | 2 / 3 / 4 |
| Security and compliance: GDPR roles and retention with the data protection officer and the accountant, processor agreements, payment provider's SCA and hosted page, PCI SAQ A, myDATA, identity setup | Confirmed data map and retention periods; processor register; threat model outline | 3 / 5 / 8 |
| Azure foundation: subscription and network model, landing-zone rules, cost governance | Agreed hosting baseline; refined cost forecast | 2 / 3 / 4 |
| **Total** | Refined backlog and a second estimate at roughly ±15% | **16 / 27 / 43** |

With the 15% for leadership: **18 optimistic, 32 expected, 49 pessimistic person-days**, about **€8k, €14k and €22k** at the
illustrative rate, over 3 to 4 weeks.

What the money buys: without this phase the delivery estimate keeps its wide range, and the three
integrations, which carry most of the uncertainty, are discovered during development, when a surprise
costs several times more.

## Cost escalation: what would make delivery cost more

These are the drivers that move the estimate beyond the commit figure. Impacts are indicative
person-days on top of the expected total, and are not cumulative guesses but separate "if" statements.

| # | Driver | Likelihood | Impact (person-days) | Early warning | Response |
|---|--------|-----------|---------------------:|---------------|----------|
| E1 | A provider API is poor: no sandbox, no idempotency, unstable, slow support | Medium | +10 to +20 per provider | Sandbox access not available in week 1 of analysis | Analyse providers first; build against recorded stubs; raise with provider early |
| E2 | Payment scope grows: capture on dispatch, partial refunds, several payment methods, stored cards or merchant-initiated charges (3-D Secure on the hosted page is in the base, package 11b) | High | +10 to +25 | Business asks "can the customer also pay with…" | Fix the launch payment scope in analysis; later methods are separate adapters |
| E3 | Tax is more complex than one rate per country: product tax classes, exemptions, several jurisdictions | Medium | +15 to +40, or a tax engine licence | More than one country or product category at launch | Decide build or buy during analysis; the pricing rule interface supports either |
| E4 | Scope added: returns, partial shipments, order amendments | Medium | +30 to +60 | Appears in workshops as "of course we need…" | Treat as a second release with its own estimate |
| E5 | Data migration from a legacy order system | Unknown | +20 to +50 | A legacy system exists with open orders | Clarify in the first workshop |
| E6 | Performance targets well above assumption A5 | Low | +10 to +25, plus higher Azure cost | Peak volumes quoted in thousands per second | Bring the broker and read model forward |
| E7 | Compliance work: formal PCI assessment, audit evidence, penetration test rework | Medium | +10 to +20 | A compliance officer joins the steering group | Keep card data out of scope (ADR-0007); schedule the penetration test early |
| E8 | Corporate Azure constraints: mandatory hub network, private endpoints everywhere, approval lead times | Medium | +5 to +15, calendar delay, and a much higher Azure bill (see the forecast) | Platform team has a queue; "everything must be private" appears as a rule | Engage the platform team during analysis; cost each private step |
| E9 | Further parties beyond one per capability: more inventory sources, more carriers, more payment providers | High | +5 to +10 per inventory source or carrier, +8 to +15 per payment provider | The list of sources and carriers is still open after analysis | The resolver and registry are built once; estimate per adapter |
| E14 | The read side grows: back-office search, reports and dashboards asked of the order views | Medium | +10 to +20 | "Can we filter orders by…" in workshops | Table views cover by-customer and by-order reads; price a SQL read model separately |
| E13 | One order supplied from several inventory sources, so shipped in several parcels | Medium | +20 to +40 | "We ship from whichever warehouse has it" | Launch with one source per order; treat split shipments as a second release |
| E11 | Distributed-system friction: local development with several apps and emulators, tracing across services, idempotency, contract versioning | Medium to high | +10 to +25 | The first end-to-end message takes more than a few days; services start sharing code or data "just this once" | Build the service template and one thin slice through two services first (package 3); keep services alike |
| E12 | Service boundaries turn out wrong and must be moved after they are separate apps with separate data | Low to medium | +15 to +40 | Two services always change together; chatty synchronous calls appear | Confirm the seven contexts in analysis; prefer merging two services early over living with a bad split |
| E15 | The checkout needs an immediate answer after all, or a push channel instead of polling | Medium | +5 to +10 for a push channel (for example Web PubSub fed by the projector); +10 to +15 to bring back a synchronous acceptance | Product owner or UX rejects "we are checking your order"; late rejections annoy customers | Show the checkout flow with "we are checking your order" in the first workshop; a GraphQL subscription fed by the projector is the push channel; measure the time to acceptance in staging |
| E16 | Messages are lost or orders stick because a handler does not follow the no-outbox rules | Low to medium | +8 to +15 to add an outbox later, plus the incident | Dead-lettered messages in staging; an order that stays in one status with no error | Failure-injection tests per handler from the first service; the dead-letter alert before go-live |
| E17 | The platform issues invoices and receipts to myDATA, rather than the ERP | Medium | +10 to +20, plus the e-invoicing provider's fee | "The shop issues the receipt" in workshops; no ERP integration exists | Decide the issuer in analysis; an adapter behind a port in Documents if it is us |
| E18 | Legal review sets stricter rules: shorter retention, a DPIA, marketing consent, data residency of a provider outside the EU | Low to medium | +5 to +15 | The data protection officer is not part of analysis | Bring the data map of [07](../architecture/07-data-protection-and-compliance.md) to the first compliance session |
| E10 | Team: late staffing, turnover, part-time allocation | Medium | +10 to +20% on calendar | Team not complete at start | Start with packages 1 to 3, which need the least domain knowledge |

### Escalation scenarios

| Scenario | What happened | Person-days | At €450 / day | Launch |
|----------|---------------|------------:|--------------:|--------|
| Commit | Normal friction, absorbed by contingency | 404 | about €182k | about 6 months |
| Escalated | Two major drivers hit, typically E2 and E1 or E3 | about 470 | about €212k | about 7.5 months |
| Severe | Scope added (E4 or E5) on top of integration problems | 555 or more | €250k or more | 9 months or more |

How escalation is controlled, in practice:

- **A decision point after analysis.** Re-estimate at roughly ±15% and confirm scope before committing the delivery budget.
- **One slice across services first.** Package 3 proves the foundation before seven services are built on it.
- **Integrations early.** Packages 5 to 7 carry the most uncertainty, so a thin end-to-end path through
  each real provider is built early, before the easy work.
- **Contingency is visible.** It is drawn down against the named drivers above and reported every sprint.
- **Scope changes are priced, not absorbed.** A new requirement gets an estimate and a decision, as in E4.

## What this estimate depends on

- The skeleton in this repository is the starting point. Starting from nothing would add roughly
  15 to 25 person-days for the structure, conventions, CI and first slice.
- Providers supply sandbox environments and technical contacts.
- A product owner is available to make decisions within days.
- The Azure subscription and identity tenant exist, or are created by a platform team.
