# ADR-0009: Azure Functions on Windows App Service plans, private endpoints only where required

- Date: 2026-11-13

## Context

The organisation's cloud is Azure. The services of
[ADR-0001](0001-microservices-and-function-per-feature.md) need a host, an entry point and a network
position. The platform uses App Service, Azure Functions and Service Bus on App Service
plans shared by several apps, which cost less than Container Apps for this shape of system, on Windows
as agreed with the organisation's DevOps team and its Azure pricing; and private endpoints only where
a requirement demands them, because their cost escalates as the system grows.

Supporting considerations:

- **One App Service plan hosts several apps.** Adding a function app to a plan does not add a compute
  bill. Container Apps charges per replica of every app.
- **Most features are triggered by messages, webhooks and timers**, which Azure Functions provides
  with retries and dead-lettering built in.
- App Service, Functions and Service Bus are the mainstream way to run .NET on Azure.
  The team lead has run App Service, Azure Functions and Service Bus in production, so the platform
  starts from operating experience, not from documentation.

## Decision

- **Every service runs as Azure Functions apps, isolated worker model**, with HTTP, Service Bus and
  timer triggers ([ADR-0001](0001-microservices-and-function-per-feature.md)).
  The isolated worker model is the only supported way forward for .NET functions: the older
  in-process model, which ran inside the Functions host on .NET Core 3.1, .NET 6 and .NET 8, reached
  the end of its support on 10 November 2026 and does not run current .NET versions. In the isolated
  model our code runs in its own process, with its own `Program.cs`, dependency injection and
  middleware, on the .NET version we choose. "Isolated" here describes the process model of one app;
  it is not what makes the services independent of each other.
- **Dedicated App Service plans on Windows.** Apps share plans; the plans are drawn by demand
  ([ADR-0010](0010-service-plans-by-demand.md)). Always On is enabled, so there are no cold starts.
- **One public entry point: Azure Front Door with WAF**, routing by path to the owning service.
  The apps accept traffic only from Front Door.
- **Identity:** Microsoft Entra for customers and staff, a managed identity per app for everything
  inside Azure ([ADR-0007](0007-security.md)). **Secrets** in Key Vault.
- **Telemetry** to Azure Monitor and Application Insights through OpenTelemetry ([ADR-0008](0008-observability.md)).
- **Networking, in three steps, each taken only when required:**
  1. *Launch:* public endpoints, locked down. Azure SQL, Storage, Service Bus and Key Vault accept only
     Entra-authenticated callers and only from the plans' subnets through VNet integration and service
     endpoints, which carry no charge.
  2. *When a requirement demands it* (compliance, a customer contract, corporate policy): private
     endpoints for the data services, each one justified and costed.
  3. *Private inbound:* an Application Gateway as the private entry point, if the API must not be
     reachable from the internet at all.

## Alternatives considered

**Azure Container Apps.** Technically sound, with scale to zero and one image for everything.
Not chosen because of the per-app cost model and the extra moving parts (registry, image builds,
another platform to learn). The code does not depend on the host, so this can be revisited.

**App Service web apps with controllers and hosted background services.** Good for HTTP-heavy work.
Not chosen as the default: integration work is triggered by messages and timers and fails for reasons
outside our control, which is what Functions triggers, retries and dead-lettering are for.

**The in-process Functions model.** Not an option: retired (see Decision).

**Functions on the Consumption or Flex Consumption plan.** Pay per execution, scale to zero.
Attractive for spiky or rare workloads. Not chosen at launch because dedicated plans give predictable
latency and VNet integration at a known price. It remains the first option for rarely used features.

**Linux plans.** At public list prices Linux App Service plans cost less than Windows plans of the
same size, because they carry no Windows licence component, and .NET runs equally on both. Not chosen:
the operating system of the plans is the organisation's decision, taken with its DevOps team, and
under the organisation's own Azure agreement Windows plans are the cheaper option. The forecast uses
public Windows list prices, so it is conservative for that agreement. The operating system is not a
design decision: the code runs on both, and switching means changing the Terraform function-app module
(`azurerm_windows_function_app` to `azurerm_linux_function_app`) and redeploying, not a redesign.

**Private endpoints everywhere from day one.** Strongest network isolation. Rejected as a default
because of cost (below); adopted where a requirement names it.

## Consequences

- The platform runs on mainstream .NET hosting that is straightforward to deploy, monitor and debug.
- Apps on a shared plan compete for the same CPU and memory. Drawing plans by demand
  ([ADR-0010](0010-service-plans-by-demand.md)) and autoscale rules are the answer.
- **The cost of going private is mostly indirect.** A private endpoint is a few euros a month. But a
  private endpoint for Service Bus requires the **Premium** tier, and private inbound needs an
  Application Gateway. Together these can cost more than the whole platform at launch. The numbers are
  in the [cost forecast](../estimation/02-azure-cost-forecast.md#the-cost-of-going-private).
- At public list prices Windows plans cost more than Linux plans of the same size; under the
  organisation's agreement they cost less (see Alternatives). Anyone comparing must use the
  organisation's prices, not the list.
- No container image and no registry: the deployment artefact is a zip package per service
  ([ADR-0011](0011-deployment.md)).
