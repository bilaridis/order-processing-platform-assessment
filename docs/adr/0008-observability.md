# ADR-0008: OpenTelemetry for traces, metrics and logs

- Date: 2026-11-13

## Context

An order's life spans an HTTP request, several services, Service Bus messages and three external
systems, over minutes or days. When a customer asks "where is my order?" or a step silently stops, the team
must be able to follow that one order through all of it.

## Decision

- **OpenTelemetry** is the single instrumentation API for traces, metrics and logs, exported over
  **OTLP**. In Azure the backend is Azure Monitor with one Application Insights resource per
  environment; locally it is any OTLP viewer, chosen with `OTEL_EXPORTER_OTLP_ENDPOINT`.
- **Tracing.** Incoming requests and outgoing HTTP calls are instrumented automatically. Each event
  handler execution is a span (`OrderProcessing.Messaging` source). The trace context travels in the
  Service Bus message properties, which the Azure SDK and the Functions trigger do by themselves when
  a handler publishes directly, so every step in every service joins the trace of the request that
  caused it.
- **Correlation.** Order id is attached to spans and log scopes. Error responses carry the trace id,
  so a support ticket leads straight to the trace.
- **Structured logging** through `ILogger` with source-generated messages; no string concatenation,
  no personal data in logs.
- **Metrics that describe the business, not just the process**: orders placed, orders by status,
  time spent in each status, rejection and cancellation rates, dead-letter count, queue age,
  latency and failure rate per external provider.
- **Health.** `/health/live` (process is running) and `/health/ready` (dependencies reachable) are
  separate, so an unavailable provider does not cause the platform to restart healthy instances.
- **Alerts** on symptoms a person must act on: **any dead-lettered message** (without an outbox it is a
  stuck order, [ADR-0012](0012-reliable-messaging-without-an-outbox.md)), orders stuck in `Submitted`, `Pending` or
  `Confirmed` beyond their expected time, circuit breakers open towards providers.

### What L1 support sees

An L1 support team works from dashboards in **Azure Managed Grafana** (or Azure Monitor Workbooks),
with Azure Monitor and Log Analytics as data sources. Grafana reads **telemetry**; it never subscribes
to Service Bus. Events are for services to act on; a dashboard that consumed them would be one more
consumer to run and to keep in step with every contract.

| Panel | Source | Code needed |
|-------|--------|-------------|
| Dead-lettered messages per queue and subscription; backlog (active messages) per queue | Service Bus **platform metrics** in Azure Monitor (`DeadletteredMessages`, `ActiveMessages`, split by entity name) | None. A ready-made Service Bus dashboard can be imported |
| Which orders are stuck, at which step, and why | One log line per dead-lettered message, written by the **dead-letter inspector** (`InspectDeadLetters`, every five minutes): entity, order id, event type, dead-letter reason, delivery count | The operations function app ([ADR-0012](0012-reliable-messaging-without-an-outbox.md)) |
| Notifications sent, delivered, failed and skipped, by channel and reason | Metrics of the notification service (`notifications.sent`, `.delivered`, `.failed`, `.skipped`) | Built in the skeleton |
| Orders by status, time in status, rejection reasons | Business metrics of Ordering | To build |
| One customer's order | The order view's timeline, through the Order API: status changes, payment, carrier detail and each notification delivered or failed | Built in the skeleton |

Fixing a stuck order is the resubmit tool: an operator puts a resubmit request on the
`operations-resubmit` queue, after the cause is fixed, following the runbook.
## Alternatives considered

**A vendor's SDK directly.** Often richer out of the box. Rejected: it ties instrumentation code to a
vendor. OpenTelemetry is supported by all major vendors, and .NET's own diagnostics map onto it natively.

**Logs only.** Rejected: reconstructing an asynchronous flow from log lines is slow and error-prone.

## Consequences

- The observability backend can be changed without touching code.
- An order is now a chain of ten or more messages. Without the trace it cannot be followed, so trace
  sampling must keep whole orders, not single spans.
- Telemetry volume costs money and grows with every service and message hop: sampling for traces is a
  deployment setting to tune ([cost forecast](../estimation/02-azure-cost-forecast.md)).
