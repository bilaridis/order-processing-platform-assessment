# ADR-0015: GraphQL for the two presentation apps, one schema per audience

- Date: 2026-11-14

## Context

[ADR-0013](0013-cqrs-app-service.md) puts a CQRS app service between the front-end apps and the core,
deployed as a customer API and a back-office API. This record decides how those two apps speak to
their clients.

- The order view is wide: lines, price, payment, carrier, tracking timeline. A list screen needs four
  fields of it, a tracking screen needs the timeline, and the back office needs most of it.
- Three front ends exist from the start (web shop, mobile app, back office) and their screens differ.
- The API already separates commands from queries, so its style should make that separation visible.
- The customer API is public. Whatever is chosen must not let a stranger explore it or load it at will.

The decision is GraphQL for both apps.

## Decision

- Each presentation app serves **its own schema**: [customer.graphql](../api/customer.graphql) and
  [backoffice.graphql](../api/backoffice.graphql). The customer schema simply has no field that reaches
  another customer's data (`myOrders`, not `ordersOfCustomer`), so the boundary is in the contract and
  not only in a policy check.
- **Mutations are commands, queries read the views.** GraphQL fits the CQRS split by its own terms. A
  resolver is thin: it reads the caller from the token and calls the same command or query handler
  that both schemas share. No resolver calls a service or decides anything about an order.
- **A client asks for the fields its screen needs.** The order view is wide (lines, price, payment,
  carrier, timeline); a list screen takes four fields of it and a tracking screen takes the timeline.
  This is what the back office gains most from, and what lets the web shop and the mobile app share one
  API with different screens.
- **What GraphQL has to be given that fixed routes had for free**: a limit on query depth and on query
  cost, introspection switched off outside development, and in production **persisted queries only**
  for the customer API (the front ends register their queries at build time and send an id), which
  also makes order reads cacheable at Front Door again with `GET`.
- **Errors a client must act on are data, not exceptions**: a mutation returns a payload with either
  the order or an `error { code message }`, using the same stable codes as the events. Transport and
  authorisation failures use the GraphQL `errors` list.
- **Push later, same schema**: a `subscription` for order status, fed by the projector, replaces
  polling without changing the read model. Not built.
- **Library: Hot Chocolate** on ASP.NET Core, two named schemas in one codebase. The schema files in
  `docs/api` are the contract, and a test fails when a running schema differs from its file.
- **Schemas evolve without versions**: fields are added, old ones are marked `@deprecated` and removed
  when no registered query uses them ([docs/api](../api/README.md)).

## Alternatives considered

**REST with an OpenAPI contract.** Simple, cacheable at the edge
without extra work, and familiar to every client developer. Not chosen: the order view
is wide and each screen needs a different part of it, the back office needs flexible lookups, and
mutation and query map directly onto the command and query sides. The price is the hardening listed
above, a server library (Hot Chocolate), and losing plain HTTP caching and status codes. For the four
operations of the customer API alone REST would have been enough; one style for both apps was preferred
over two. REST remains for what is not client API: provider webhooks and health checks
([openapi.yaml](../api/openapi.yaml)).

**One GraphQL schema for both audiences**, with staff fields guarded by a role. Fewer files. Rejected:
the customer app would publish the shape of staff operations, and one wrong policy would expose them.

**GraphQL for the back office only, REST for the customer API.** The customer API has four operations
and gains least from GraphQL, while it pays most for the hardening because it is public. This was the
architect's recommendation. Not chosen: two styles mean two contracts, two error models and two sets
of client tooling for one small API.

**A GraphQL gateway that federates the services** (each service publishes its own subgraph). Rejected:
it would put HTTP between the presentation layer and every service, against
[ADR-0003](0003-event-driven-communication.md), and reads would load the core again.

## Consequences

- One request style for every client, and each screen takes only the fields it needs.
- **The customer API must be hardened before it is public**: depth and cost limits, no introspection,
  persisted queries only. Persisted queries are designed and not built, and without them a public
  GraphQL endpoint is the first thing a security review will question.
- Plain HTTP caching and status codes are lost. Errors a client acts on are in the payload, so
  monitoring cannot count failures from status codes and must count error codes instead
  ([ADR-0008](0008-observability.md)).
- The team needs GraphQL knowledge on the server and in three front ends. If it has none, this costs
  the first weeks ([estimate](../estimation/01-high-level-estimate.md)).
- N+1 reads are not a risk today, because a query reads one view row with everything in it. They
  become one the day a resolver reads a second store, and then a data loader is required.
