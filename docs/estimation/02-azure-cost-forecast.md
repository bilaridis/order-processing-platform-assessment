# 02 Azure cost forecast

What the platform costs to run on Azure, how that changes with volume, and where the bill jumps.
The architecture being priced is the [Azure reference architecture](../architecture/05-azure-reference-architecture.md):
seven independent services ([ADR-0001](../adr/0001-microservices-and-function-per-feature.md)) and a CQRS app service ([ADR-0013](../adr/0013-cqrs-app-service.md)), deployed as thirteen apps,
on Windows App Service plans that are split by demand ([ADR-0010](../adr/0010-service-plans-by-demand.md)); Service Bus, Azure SQL, Table and Blob storage.

## Basis and confidence

- Region **West Europe**, currency **EUR**, pay-as-you-go list prices, no reservations or discounts, VAT excluded.
- A month is 730 hours.
- **Unit prices marked ✔ were read from the Azure Retail Prices API on 2026-11-13.**
- **Prices marked ≈ could not be retrieved in the authoring environment.** They are indicative figures
  from general knowledge of Azure pricing and may be off by tens of percent. This applies to the two
  largest lines, the App Service plan and Azure SQL. **Re-run this forecast in the
  [Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/) before using it for a budget.**
- The sizing is an engineering judgement, not a measurement. Nothing has been load tested. Expect
  ±30% on the totals until staging has run a load test.

### Unit prices used

| Service | Meter | Price | Per month | Source |
|---------|-------|------:|----------:|:------:|
| App Service plan, Windows | B1 (1 core, 1.75 GB) / B2 (2 cores, 3.5 GB) | | ≈ €50 / ≈ €100 | ≈ |
| App Service plan, Windows | P0v3 (1 vCPU, 4 GB) per instance | | ≈ €110 | ≈ |
| App Service plan, Windows | P1v3 (2 vCPU, 8 GB) / P2v3 (4 vCPU, 16 GB) per instance | | ≈ €220 / ≈ €440 | ≈ |
| Azure Functions | On the same dedicated plan | no additional charge | €0 | |
| Azure SQL Database, DTU model | S0 / S1 / S2 / S3 | | ≈ €13 / €26 / €65 / €130 | ≈ |
| Azure SQL Database, vCore General Purpose | 2 / 4 / 8 vCores | | ≈ €340 / €680 / €1,360 | ≈ |
| Service Bus | Standard base | €0.0121 per hour | €9 | ✔ |
| Service Bus | Premium messaging unit | €0.8326 per hour | €608 | ✔ |
| Table storage | Data / transactions | ≈ €0.04 per GB / ≈ €0.0003 per 10,000 | a few euros | ≈ |
| Blob storage, hot | Data | ≈ €0.02 per GB | a few euros | ≈ |
| Private endpoint | Per endpoint / data processed | ≈ €0.009 per hour / ≈ €0.009 per GB | ≈ €7 each | ≈ |
| Application Gateway WAF v2 | Fixed plus capacity units | | ≈ €250 to €330 | ≈ |
| Front Door | Standard base fee / Premium base fee | | ≈ €32 / ≈ €300 | ≈ |
| Front Door | Standard requests / data transfer out | €0.0079 per 10,000 / €0.1496 per GB | | ✔ |
| Log Analytics | Ingestion after the first 5 GB | €2.6311 per GB | | ✔ |
| API Management | Developer / Basic v2 / Standard v2 / Premium | €0.0579 / €0.1808 / €0.8438 / €3.3694 per hour | €42 / €132 / €616 / €2,460 | ✔ |
| Key Vault | Standard operations | €0.0258 per 10,000 | under €5 | ✔ |
| Entra External ID | Monthly active users | first 50,000 free | €0 | ≈ |

## Volume scenarios

| | Launch | Growth (×10) | Scale (×100) |
|--|-------:|-------------:|-------------:|
| Orders per day | 5,000 | 50,000 | 500,000 |
| Orders per month | 150,000 | 1.5 million | 15 million |
| API requests per month (edge budget of 10 per order, see below) | 1.5 million | 15 million | 150 million |
| Service Bus operations per month (about 45 per order: every request between services is a command and an event, and each event goes to several subscriptions) | 6.8 million | 68 million | 675 million |
| Telemetry ingested per day (seven services, traces across Service Bus) | 0.8 GB | 7 GB | 40 GB |

### Demand profile behind the plans

| Feature | Expected demand | Plan (demand tier) |
|---------|-----------------|--------------------|
| Delivery offers at checkout; receive an order, quote it, reserve its stock and accept it | Every order, while the customer waits for the result: the volume | Order intake |
| Payment, shipment, order process after acceptance, customer notifications | Every order, nobody waiting, levelled by Service Bus | Order flow |
| Retrieve an order, list orders, follow a delivery | Served by the customer API from the order views in Table storage; frequency to confirm in analysis | Order intake (the customer API web app) |
| Back-office lookups and cancellations by staff | A few support agents | On demand (the back-office API web app) |
| Cancel an order | Exceptional; its own `CancelOrder` message on Ordering's command queue | Order intake (in `ordering-intake`) |
| Documents, ERP, the operations tool | Occasional | On demand |

The edge budget of 10 requests per order is kept on purpose: it prices Front Door and telemetry as if
retrieval were *not* exceptional, so those lines do not move if the assumption turns out wrong. Only
the plan that hosts the customer API would (escalator C2c).

## Production, per month

Thirteen apps (eleven function apps and the two web apps of the Order API, customer and back office) on plans drawn by demand. At launch there are two plans: order intake on its own,
order flow and on-demand work together. Isolation between apps on a plan is by app, identity and data.

| Service | Launch | Growth | Scale |
|---------|-------:|-------:|------:|
| **App Service plans** | two plans (intake 2 × P0v3; flow and on demand 2 × P0v3): **€440** | three plans (intake 2 × P1v3, flow 2 × P1v3, on demand 2 × P0v3): **€1,100** | three plans (intake 4 × P2v3, flow 4 × P1v3, on demand 2 × P1v3): **€3,080** |
| **Azure SQL Database** (Ordering) | S2: **€65** | General Purpose 2 vCores: **€340** | General Purpose 8 vCores, with backup and storage: **€1,600** |
| **Geo-secondary database** for a 15-minute recovery point (active geo-replication to the paired region, same tier) | S2: **€65** | **€340** | **€1,600** |
| **Service Bus** | Standard: **€10** | Standard: **€55** | Premium, 1 unit: **€608** |
| **Storage** (tables, blobs, order views) | **€5** | **€15** | **€60** |
| **Front Door + WAF** | Standard: **€35** | Standard: **€60** | Premium: **€550** |
| **Azure Monitor** | 25 GB: **€53** | 200 GB, partly sampled: **€350** | 1,200 GB, sampled, commitment tier: **€1,500** |
| **API Management** | none: **€0** | none: **€0** | Standard v2: **€616** |
| **Private endpoints** | none: **€0** | none: **€0** | 12 endpoints and data: **€120** |
| Key Vault and other | **€5** | **€10** | **€50** |
| **Production total** | **≈ €675** | **≈ €2,240** | **≈ €9,800** |
| Plausible range | €550 to €850 | €1,900 to €2,800 | €8,600 to €12,600 |
| Infrastructure cost per order | €0.0045 | €0.0015 | €0.00065 |

Reading the table:

- At launch, **the App Service plans are about two thirds of the bill**.
- **The recovery point costs a second database.** Losing at most 15 minutes of orders when a region
  is lost needs a geo-secondary of the Ordering database; geo-redundant backups alone allow up to an
  hour. It is one tenth of the launch bill and grows with the database tier.
- **Splitting by demand does not change the launch total.** The same four vCPUs and 16 GB are bought as
  two plans of two small instances instead of one plan of two larger ones. What changes is that order
  placement has capacity nothing else can take.
- The plans grow unevenly, which is the point: from launch to scale the intake plan grows eight times
  in price, the flow plan four times, and the on-demand plan only when measurements ask for it.
- **The CQRS app service adds no plan at launch**: the web app runs on the intake plan and its
  projector on the second plan. Its order views in Table storage cost a few euros.
- Memory is the constraint to watch on the small plans: eight apps on 4 GB instances in the second
  plan, five in the first. The load test decides whether they stay at P0v3; one size up for the intake
  plan is +€220 a month (escalator C2e).
- Function apps, Table storage and Blob storage add almost nothing per service. What a new service
  really costs is memory on the plan and telemetry.
- Volume grows 100 times, cost grows about 15 times.
- The step from Growth to Scale is mostly **tier changes, not usage**: Service Bus Premium, Front Door
  Premium, API Management and the vCore database.

### What microservices cost compared with a single host

| | Single host (for comparison) | Microservices, plans by demand | Microservices, a plan per service |
|--|--:|--:|--:|
| Production at launch | ≈ €435 | ≈ €675 | ≈ €1,765 |
| All environments at launch | ≈ €675 | ≈ €1,070 | ≈ €2,565 |

Independence of deployment, identity and data is achieved in the middle column. The right-hand column
adds independence of hardware for every service, and is where "one service, one plan" as a rule leads.
The middle column buys hardware separation only where demand differs: order intake apart from the rest.

## Notifications: email, SMS and push

The notification service ([06](../architecture/06-integration-services.md#notification-integration-service-email-sms-push))
uses Azure Communication Services for email and SMS and Azure Notification Hubs for push. Both are
billed on the Azure invoice, by usage. These figures are **indicative and unverified**, and they depend on a policy the business has
not set yet: which events send which channel.

| Channel | Launch volume | Launch, per month | Growth (×10), per month |
|---------|--------------:|------------------:|------------------------:|
| Email, ACS (4 per order) | 600,000 | about €150 to €300 | about €1,500 to €3,000 |
| SMS, ACS, Greek numbers (1 per order, about €0.05 to €0.08 each) | 150,000 | **about €7,500 to €12,000** | **about €75,000 to €120,000** |
| Push, Notification Hubs (Basic, then Standard) | 750,000 | about €10 | about €200 |

**SMS is the largest single running cost of the platform**, larger than all of Azure at launch. It
scales linearly with orders and has no tier that flattens it. The launch policy caps it: at most one
SMS per order, for "out for delivery" or "ready for collection", only when push did not reach the
customer. The table assumes the cap is reached on every order; every order that push reaches lowers it.

## Non-production, per month

| Environment | Shape | Cost |
|-------------|-------|-----:|
| Development / test | One B2 plan for all apps (no split: there is no demand to separate), SQL S0, Service Bus Standard, minimal storage, free telemetry allowance | ≈ €125 |
| Staging | Two plans as in production, 1 × P0v3 each, SQL S1, Service Bus Standard, small telemetry | ≈ €270 |

## Total at launch

| | Per month | Per year |
|--|----------:|---------:|
| Production | ≈ €675 | ≈ €8,100 |
| Staging | ≈ €270 | ≈ €3,200 |
| Development / test | ≈ €125 | ≈ €1,500 |
| **All environments** | **≈ €1,070** | **≈ €12,800** |
| Range | €850 to €1,350 | €10,200 to €16,200 |

In the first year, running cost is under a tenth of the delivery cost in the
[high-level estimate](01-high-level-estimate.md).

## The cost of going private

Private endpoints are the clearest example of a cost that looks small and is not. One endpoint is
about €7 a month plus a small charge per GB. The real cost is in what private networking **requires**
and in how the endpoints **multiply**.

| Step | What is added | Added per month at launch | Production total |
|------|---------------|--------------------------:|-----------------:|
| 1. Launch position | Public endpoints locked down with Entra authentication, access restrictions, VNet integration and service endpoints | €0 | ≈ €610 |
| 2. Private data plane | Private endpoints for Azure SQL, Blob, Table and Key Vault, with private DNS zones | ≈ €30 | ≈ €640 |
| 3. Private messaging | A private endpoint for Service Bus is only available on **Premium** | ≈ €600 | ≈ €1,240 |
| 4. Private inbound | Application Gateway with WAF as the private entry point; a private endpoint for each of the thirteen apps | ≈ €355 | ≈ €1,600 |

So a fully private platform costs roughly **two and a half times** the launch figure, and none of
that increase comes from traffic.

How the endpoint count escalates as the system grows:

- **Per environment.** Whatever is private in production is normally private in staging too, or the
  test is not representative: multiply by two or three.
- **Per service.** Every storage sub-service (blob, table, queue, file) needs its own endpoint.
  Every new database, namespace or vault adds one.
- **Per microservice.** Each service with private inbound access adds an endpoint, and each service
  with its own database, vault or storage account adds more. With seven services this is the fastest-growing term.
- **Per GB.** Data processed through endpoints is charged, so the cost follows traffic as well.
- A platform that starts with 4 endpoints can reach 30 to 40 across services and environments within
  a year: about €200 to €300 a month for endpoints alone, before the Premium tiers they force.

Policy for this design ([ADR-0009](../adr/0009-azure-hosting.md)): start at step 1, which
already blocks anonymous and off-network access at no cost; take each further step only against a
named requirement, with this table as the price.

## Other cost escalators

| # | Escalator | Trigger | Effect | How to contain it |
|---|-----------|---------|--------|-------------------|
| C1 | **Telemetry volume** | Verbose logging, 100% trace sampling; multiplied by seven services and every Service Bus hop | The most common surprise. 5 GB a day instead of 0.8 adds about €330 a month at launch | Sampling from day one, daily cap on non-production, alert on ingestion |
| C2 | **A plan per service** | "Isolated" read as separate hardware for each service | Compute goes from about €440 to about €1,540 a month at launch for the same traffic | Plans by demand tier ([ADR-0010](../adr/0010-service-plans-by-demand.md)), not by service |
| C2c | **Heavy read load** | Clients poll for order status or refresh a tracking page; reads far outnumber orders | Reads are served from Table storage by the customer API, so the core is not affected. The query side may need its own app and plan: about +€220 a month at launch | Ask client teams in analysis; a short cache lifetime on the `order` query at Front Door; push status by notifications |
| C2e | **Memory on the small plans** | Thirteen apps on two plans of 4 GB instances | One size up for a plan: +€220 a month each | Measure in the load test before launch; keep hosts lean |
| C2d | **Too many small plans** | A new tier or plan for every exception | Each plan has a minimum of two instances; idle capacity cannot be shared between plans | Three tiers at most; a feature joins an existing tier unless measurements say otherwise |
| C2b | **Plan tier** | Scaling up instead of out; Premium tiers for features | Each step doubles the instance price | Scale out with autoscale limits |
| C3 | **Plan prices differ from the list** | Pricing agreement | This forecast uses public Windows list prices. Under the organisation's Azure agreement Windows plans are cheaper than Linux, so production compute is likely lower than shown; at list prices Linux would be the cheaper one | Replace the list prices with the organisation's prices before the budget is fixed. The operating system is a parameter of the Terraform module, not a design decision (ADR-0009) |
| C4 | **Azure SQL model change, and a database per service** | Outgrowing DTU tiers; moving to vCore; Business Critical; more services wanting relational storage | S3 to General Purpose is roughly 2.5 times; Business Critical a further 2.5 to 3 times; each extra database repeats the base cost | Tune queries first (Query Store), archive old orders; Table storage for services that only need keyed data; an elastic pool if several small databases appear |
| C5 | **Service Bus Standard to Premium** | Private endpoints, messages over 256 KB, dedicated throughput | From about €9 to €608 a month per unit | Small messages; private networking as a costed decision |
| C6 | **API Management** | Third-party API consumers | €132 to €2,460 a month depending on tier | Not needed for first-party clients |
| C7 | **Front Door Standard to Premium** | Managed WAF rule sets, bot protection, private origin | From about €32 to about €300 a month base | Decide with security in analysis |
| C8 | **Second region** | A recovery-time objective a single region cannot meet | Roughly +60% to +100% of production | Agree recovery objectives first; geo-backups may be enough |
| C9 | **Number of environments** | One per team or branch | €125 to €270 each per month, permanently | Share development; delete what is not used |
| C10 | **Blob storage growth and egress** | Photos kept forever; large downloads | Slow and steady; storage is cheap, retrieval and egress less so | Lifecycle rules to cool and archive tiers; retention periods |
| C11 | **Security add-ons** | Defender for Cloud plans, including malware scanning of uploads | Per-resource and per-GB charges | Enable what the threat model needs |
| C12 | **Idle resources** | Non-production running at night, forgotten databases | Waste | Schedules, owner tags, monthly review |

### Escalation scenarios for production

| Scenario | What changed | Per month |
|----------|--------------|----------:|
| Launch, as planned | | ≈ €675 |
| Launch, careless telemetry | C1 | ≈ €1,005 |
| Launch, reads are heavy and the query side gets its own plan | C2c | ≈ €895 |
| Launch, intake plan one size up for memory | C2e | ≈ €895 |
| Launch, a plan per service | C2 | ≈ €1,765 |
| Launch, fully private | Steps 2 to 4 | ≈ €1,665 |
| Launch, with a partner API | C6, Standard v2 | ≈ €1,295 |
| Launch, a plan per service and fully private | C2 and steps 2 to 4 | ≈ €2,815 |
| Growth, as planned | | ≈ €2,240 |
| Growth, fully private with a warm second region | Steps 2 to 4 and C8 | ≈ €4,500 to €6,000 |
| Scale, as planned | | ≈ €9,800 |
| Scale, Business Critical database and a warm second region | C4 and C8 | ≈ €16,000 to €20,000 |

The same order volume can cost two to four times as much depending on decisions that have nothing to
do with traffic. These are decisions to take consciously in the analysis phase.

## Keeping cost under control

1. **Budgets and alerts** per environment in Azure Cost Management, at 50%, 80% and 100% of the forecast, sent to the team lead.
2. **Tags** on every resource: environment, service, owner.
3. **Autoscale with limits.** A maximum instance count per plan, so a bug or an attack cannot scale the bill without bound.
4. **Telemetry discipline.** Sampling, log levels by environment, ingestion alert.
5. **Commitment discounts once stable.** After two to three months of real usage: reserved instances for
   the App Service plan and reserved capacity for Azure SQL. Confirm the current rates in the calculator.
6. **A monthly ten-minute cost review**: actual against this forecast, top three movers, one action.
7. **Cost per order as a metric**, tracked next to latency and error rate.

## What would refine this forecast

- The real unit prices for the lines marked ≈, from the pricing calculator or the organisation's agreement.
- Agreed volumes and peaks from the analysis phase.
- A load test on staging, to replace the sizing assumptions with measurements.
- Decisions on private networking, recovery objectives and third-party API access.
