# ADR-0002: Service structure, clean architecture and enforced boundaries

- Date: 2026-11-13

## Context

Independent services ([ADR-0001](0001-microservices-and-function-per-feature.md)) are only independent
while their code does not depend on each other's internals. We need to decide what a service looks
like on disk, what other services may see of it, how much internal structure each one gets, and how
the rules are kept true over time. In the skeleton a service is called a *module*: the same projects,
composed into one local host.

## Decision

**Public surface.** Every service has a `OrderProcessing.X.Contracts` project containing its public
interface (`IXApi`), DTOs, commands and integration events. It is published as a versioned package and
is the only thing another service may reference.

**Clean architecture inside a service, proportional to its complexity.**

- *Ordering* is the core domain with real business rules, so it has all the layers:
  `Domain` (aggregate, no dependencies) ← `Application` (commands, handlers, ports) ← `Infrastructure`
  (persistence, messaging), `Module` (the entry point that registers the service) and `Functions` (the Azure host).
- *Pricing* is a rule pipeline and the *integration services* are adapters. Each starts as a single
  implementation project containing its handlers, a port, its adapter and the registration.
  A service is split into layers when its logic outgrows one project, not before.
- The dependency rule is the same everywhere: domain depends on nothing, handlers depend on ports,
  Azure SDKs appear only in adapters.

**Visibility.** Types are `internal` unless they are in `Contracts` or `Domain`, or are the service
entry point (`XModule : IModule`) or its options class.

**Composition.** A host knows a service only as `IModule` (register services, map endpoints). The local
host of the skeleton loads all of them; in Azure each Functions host loads one.

**Enforcement.** Architecture tests inspect the compiled assemblies and fail the build if:
a service references another service's non-contract assembly; a `Contracts` or `Domain` project takes
a disallowed dependency; `Application` references web or persistence frameworks; or an implementation
project exposes unexpected public types. The rules key off the assembly naming convention
`OrderProcessing.{Service}[.{Layer}]`, so new services are covered without touching the tests.
Deployment enforces the same boundary a second time: separate apps, identities and data stores.

## Alternatives considered

**The same four layers for every service.** Uniform and predictable. Rejected because it produces
twenty projects, most of them holding one or two files, and teaches the team that structure is ceremony.

**Folders and namespaces inside one project per service.** Fewer projects.
Rejected for the core: a project reference is the cheapest boundary the compiler enforces, and
`internal` only means something across assemblies.

**Convention and code review only.** Rejected: boundary violations are small, convenient and easy to
approve under deadline. A failing test is not negotiable.

## Consequences

- A new engineer can see what a service offers by opening one small project.
- Services differ in shape, which must be explained once (here).
- Dependency rules are verified on every build at the assembly level. Finer rules (for example on
  namespaces inside a single-project service) would need an additional tool such as NetArchTest.
- `InternalsVisibleTo` is used between a service's own layers and for its tests. It must never name another service.
- Contract packages are versioned and changed additively, because a consumer may deploy later than its publisher.
