# Source

```
Host/OrderProcessing.Host                   Local composition root: all modules in one process, in-memory infrastructure.
Host/OrderProcessing.Operations.Functions   Operations tool: dead-letter inspector and resubmit.
BuildingBlocks/                             Small shared abstractions (commands, events, results). No business logic.
Modules/
  Ordering/        Core. Contracts, Domain, Application, Infrastructure, Module, Functions.
  Pricing/         Core. Quote as a pipeline of pricing rules.
  Inventory/       Integration service: stock from several sources.
  Ecommerce/       Integration service (ecommerce-integration-service): payment providers. Has a Functions host.
  Carriers/        Integration service (carrier-integration-service): offers, booking, tracking. Commands and events are named for shipping.
  Notifications/   Integration service: email and SMS (ACS), push (Notification Hubs). Has a Functions host.
  OrderApi/        The Order API: two GraphQL schemas, mutations answer "received", queries read views.
```

Each module becomes its own service on Azure, one function per feature
([ADR-0001](../docs/adr/0001-microservices-and-function-per-feature.md)). Here they share one host, and
still talk only through messages on an in-memory bus.

Enforced by `tests/OrderProcessing.ArchitectureTests`:

- A module references another module only through its `*.Contracts` project.
- Types are `internal` unless they are contracts, domain types or the module entry point.
- The host references module entry points and nothing inside them.

Classes marked `SKELETON PLACEHOLDER` are in-memory stand-ins for infrastructure; the
[implementation guide](../docs/architecture/04-implementation-guide.md) lists what replaces each.
