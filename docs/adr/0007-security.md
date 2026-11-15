# ADR-0007: Token-based security, deny by default

- Date: 2026-11-13

## Context

The platform holds personal data (names, addresses, purchase history) and triggers payments. Callers
are customers through first-party apps and staff through a back office. External systems call in
through webhooks.

## Decision

**Authentication is delegated.** Microsoft Entra (External ID for customers, Entra ID for staff) issues OAuth 2.0 / OpenID Connect tokens. The
platform validates JWT bearer tokens (issuer, audience, signature, lifetime) and stores no credentials.

**Deny by default.** A fallback authorization policy requires an authenticated caller on every endpoint.
Anonymous access (health probes, webhooks) is an explicit opt-out that shows up in code review.

**Authorization has two levels.**

- *Scope*: `orders.read`, `orders.write` for customers; `orders.manage` for back-office access to any
  order. Each service declares its own policies.
- *Ownership*: the customer id is taken from the token subject, never from the request body.
  Use cases check that the order belongs to the caller. Someone else's order answers as not found (`null`), not as forbidden,
  so identifiers cannot be probed.

**Webhooks are authenticated on their own terms**: the provider's HMAC signature over the raw body with
a timestamp to prevent replay. The skeleton uses a shared secret compared in constant time as a stand-in.

**Payment data.** Card data is entered only on the payment provider's hosted page (or its own iframe
fields), where 3-D Secure also runs. The platform handles only provider references and outcomes, never a
card number or a card token, which keeps it at PCI DSS SAQ A
([ADR-0014](0014-personal-data-and-payment-regulation.md)).

**One public surface, split by audience.** Front-end apps call only the Order API
([ADR-0013](0013-cqrs-app-service.md)), deployed as two apps: the **customer API** accepts only
Entra External ID tokens and acts only on the caller's own orders; the **back-office API** accepts only
staff tokens from Entra ID with app roles, and its Front Door route can be limited to the company's
network. Each validates the token and passes the caller's identity and rights on in the command. Core
services accept commands only from these two managed identities and still check ownership themselves.

**Service to service.** Calls between services and to Azure resources use managed identities and Entra
tokens. A service accepts only the callers it names.

**Secrets and configuration.** Secrets come from Key Vault, never from source
control. `appsettings.Development.json` contains only local, non-sensitive values.

**Input and output.** Requests are validated at the edge and again by the aggregate. Errors carry a
stable code and never include stack traces or internals: an `error` field in the payload of a GraphQL
mutation, and RFC 9457 problem details with a trace id on the few HTTP endpoints (webhooks, health).

**Transport and runtime.** TLS terminates at the edge (Front Door) and is enforced end to end. Services
authenticate to Azure resources with managed identities; no connection string contains a secret.

**Infrastructure.** Every Azure resource is declared in Terraform, reviewed as a plan and scanned for
misconfiguration on each pull request ([ADR-0011](0011-deployment.md)). This complements the
penetration test of the running system; it does not replace it.

**Personal data.** What is kept where, for how long, and how it is erased is decided in
[ADR-0014](0014-personal-data-and-payment-regulation.md) and mapped in
[07](../architecture/07-data-protection-and-compliance.md). Telemetry never carries personal data.

Still to add during delivery: rate limiting per client, audit logging for back-office actions,
dependency scanning in CI.

## Alternatives considered

**API keys.** Simple, but they identify an application, not a customer, and cannot carry ownership.
Suitable later for third-party integrations, in addition to tokens.

**Authorization only at an API gateway.** A gateway is useful for coarse checks and rate limits.
Rejected as the only layer: ownership is a business rule and must hold even for calls that bypass the edge.

**Building identity into the platform.** Rejected: identity is a product in itself and not our domain.

## Consequences

- The platform depends on an identity provider being available for token issuance (not for each request;
  signing keys are cached).
- Scope names are part of the contract with the identity provider and are documented with the API contracts ([docs/api](../api/README.md)).
- Local development uses `dotnet user-jwts`, so the same authentication code runs on a laptop and in production.
