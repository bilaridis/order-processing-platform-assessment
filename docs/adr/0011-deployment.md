# ADR-0011: Deployment: Terraform for infrastructure, one package per service from an artefact repository

- Date: 2026-11-13

## Context

The platform must be deployable repeatably, scale horizontally and be updated without downtime.
Each service is released independently ([ADR-0001](0001-microservices-and-function-per-feature.md))
to Azure Functions apps on App Service plans ([ADR-0009](0009-azure-hosting.md)).

## Decision

- **One immutable zip package per service per commit**, built once in CI, **stored in an artefact
  repository** under its version, and promoted unchanged through environments. Every environment
  deploys the same file from the repository; nothing is rebuilt. Deployed with run-from-package. A service with several function apps
  ([ADR-0010](0010-service-plans-by-demand.md)) deploys them from the same build, together.
- **A pipeline per service**, triggered by changes to that service or to a contract it publishes.
  Shared pipeline templates keep the nine pipelines (seven services, the Order API with its two web apps and projector, and the operations tool) identical in shape.
- **Zero-downtime releases through staging slots.** Deploy to the slot, warm up, check health, swap.
  Rolling back is swapping back. This is safe because handlers are idempotent and apps are stateless.
- **Configuration from the environment** (twelve-factor): app settings per app and slot, secrets as
  Key Vault references, identities instead of connection-string secrets.
- **Stateless apps.** No in-memory session or cache that matters; any instance can serve any request
  or message. Scale by adding instances to the plan.
- **Database migrations are a separate, explicit step**, run by the pipeline with its own identity
  before the swap, never at application start-up, and always backward compatible with the previous
  version of the code ([ADR-0004](0004-data-ownership-and-storage.md)).
- **Infrastructure as code in Terraform** (`azurerm` provider). See the
  section below.
- **GitHub Actions with OIDC** to Azure: no stored deployment credentials.
- **Pipeline stages.** Pull request: restore, build with warnings as errors, format check, unit and
  architecture tests, package. Main: the same, then deploy to the test environment; promotion to
  staging and production is a gated step.
- **Compatibility between services.** Contracts change additively, so services can be deployed in any
  order. A breaking change is a new version of the message or endpoint, with both served during the transition.

### Infrastructure as code with Terraform

- **Everything in Azure is declared in Terraform**: plans, apps and slots, app settings, managed
  identities and role assignments, Service Bus topics, subscriptions and queues, Azure SQL, storage
  accounts and tables, Key Vault, Front Door, monitoring and alerts. One module per service, one
  root configuration per environment; the plan of each app is a variable.
- **Every change is reviewed as a plan.** A pull request that touches Terraform runs `terraform plan`
  and posts the result, so the reviewer sees exactly what will be added, changed or destroyed before
  approving. `terraform apply` runs only from the main branch, behind an approval for production, and
  applies the plan that was reviewed.
- **State is remote and protected**: an Azure Storage backend per environment, with locking, versioning
  and access limited to the pipeline identity. State can contain sensitive values, so it is treated as
  a secret. Nobody applies from a laptop.
- **Infrastructure code is scanned on every pull request** by a static analysis tool for Terraform
  (for example Checkov or Trivy) and by policy rules of our own: no public blob access, TLS versions,
  no storage keys, diagnostic settings on, private endpoints where the environment requires them.
  A finding fails the build.
- **This is not the penetration test.** Scanning finds misconfiguration in what we *declare*. A
  penetration test attacks what is *running* and finds what no template shows: flaws in the API, in
  authorisation, in the way parts combine. Both are done ([ADR-0007](0007-security.md)); the Terraform
  code and the scan results are given to the testers as input, which makes their test faster and deeper.
- **Terraform provisions; the pipeline releases.** Terraform creates the apps and their settings. It
  does **not** deploy application code. The zip package is deployed to the staging slot by the release
  pipeline with the Azure CLI (`az functionapp deployment source config-zip`, `az webapp deploy`) and
  then swapped. Terraform is told to ignore the package setting and the slot's current content, so a
  release is never an infrastructure change and an infrastructure change is never a release.
- **Artefact repository.** Zip packages and the `Contracts` NuGet packages are published to one
  repository with retention and immutability rules: JFrog Artifactory if the organisation runs it,
  otherwise GitHub Packages with a versioned blob container for the zips. Terraform modules are
  versioned and consumed from there as well.

## Alternatives considered

**Bicep.** Azure's own language: no state file to protect, support for new Azure features on the day
they appear, `what-if` for review, nothing to license. A sound choice for an Azure-only platform.
Terraform is chosen for the reviewable plan, the mature scanning and policy tooling,
and because one tool can also manage what is not Azure Resource Manager (Entra applications, GitHub
settings, DNS). The price is the state file and a provider that can lag behind new Azure features.
Whether the team already writes Terraform is not a deciding factor: with AI assistance the language
is the cheapest part to learn, for this tool as for any other. What the team must master is the
**operating model**: remote state with locking, reading a plan before it is applied (a replace or a
destroy hidden in a long plan), drift, imports and provider upgrades. That needs judgement, not syntax,
and it is what the pipeline enforces: the plan is reviewed in the pull request, scanned, and applied
only by the pipeline identity.
The team lead knows Terraform and coaches the team on it. To avoid one person being
the only one who can read a plan, the first modules are written in pairs and every production plan
needs a second reviewer besides the lead.

**Deploying the application package with Terraform** (for example `zip_deploy_file`). One tool for
everything. Rejected: it ties every release to an infrastructure apply, fights with slot swaps, and
puts routine releases behind the heavier infrastructure approval.

**Infrastructure created by hand or by scripts** (portal, ad hoc `az` commands). Rejected: not
reviewable, not repeatable, and environments drift apart.

**Pipeline artefacts only, no repository.** Simple. Rejected: pipeline artefacts expire, and a rollback
or an audit needs the exact package that ran in production months ago.

**Container images.** Portable across clouds. Not chosen: the hosting decision is App Service and
Functions, where a zip package is simpler and needs no registry.

**Migrations at start-up.** Convenient. Rejected: with several instances starting at once it races, and
it gives the application's database identity schema-change privileges it should not have.

**One pipeline that deploys all services together.** Simpler to set up. Rejected: it gives up the
independent releases that the service split exists for.

## Consequences

- The same artefact that passed the tests is what runs in production.
- A change to one service redeploys only that service.
- Nine pipelines and thirteen apps to keep consistent; pipeline templates and Terraform modules carry that.
- Schema changes need discipline (expand, migrate, contract over two releases).
- The Terraform is in [`deploy/terraform`](../../deploy/terraform/README.md): the shared platform and the
  Ordering service end to end, with the release pipeline of Ordering as the template for the others. It
  is validated, tested with a mocked provider and scanned, but not yet applied to a subscription. The
  other services' modules and pipelines and the artefact repository are still to build.
- Terraform state is one more thing to secure, back up and keep consistent. A lost or corrupted state
  file is an incident.
- Terraform's licence changed to the Business Source Licence in 2023. For internal use this has no
  practical effect; OpenTofu is the open-source alternative if the organisation's policy requires one.
