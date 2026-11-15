# Architecture Decision Records

One record per significant decision: context, decision, alternatives considered, consequences. Each
record describes the current design; options not taken are under "Alternatives considered".

| # | Decision |
|---|----------|
| [0001](0001-microservices-and-function-per-feature.md) | Microservices from the start, one function per feature with its command and handler |
| [0002](0002-service-structure-and-boundaries.md) | Service structure, clean architecture, enforced boundaries |
| [0003](0003-event-driven-communication.md) | Service Bus between services: command queues, event topics, no HTTP |
| [0004](0004-data-ownership-and-storage.md) | Data per service: Azure SQL, Table storage, Blob storage with SAS links; the Documents service |
| [0005](0005-order-placement-flow.md) | Receive the order at once (`Submitted`), accept it after price and stock, then pay and ship |
| [0006](0006-external-integrations.md) | Four integration services, each one contract in front of many parties |
| [0007](0007-security.md) | Token-based security, deny by default |
| [0008](0008-observability.md) | OpenTelemetry for traces, metrics and logs |
| [0009](0009-azure-hosting.md) | Azure Functions on Windows App Service plans, private endpoints only where required |
| [0010](0010-service-plans-by-demand.md) | App Service plans by demand: order intake, order flow, on demand |
| [0011](0011-deployment.md) | Terraform with reviewed and scanned plans, one package per service |
| [0012](0012-reliable-messaging-without-an-outbox.md) | No outbox: trigger redelivery, send from state, inbox |
| [0013](0013-cqrs-app-service.md) | A CQRS Order API in front of the core, as a customer API and a back-office API |
| [0014](0014-personal-data-and-payment-regulation.md) | GDPR, PSD2 and PCI DSS: hosted payment page with 3-D Secure, personal data mapped and erasable |
| [0015](0015-graphql-for-the-presentation-apps.md) | GraphQL for both APIs, one schema per audience |

Reading order: 0001 sets the shape; 0002 to 0004 how services are built, talk and store data; 0005 to
0008 the order flow and cross-cutting concerns; 0009 to 0011 Azure; 0012 reliability; 0013 and 0015 the
API in front; 0014 regulation.

When a decision changes, its record is edited and the dropped option moves to "Alternatives considered".

## Template

```markdown
# ADR-NNNN: Title

- Date: YYYY-MM-DD

## Context
## Decision
## Alternatives considered
## Consequences
```
