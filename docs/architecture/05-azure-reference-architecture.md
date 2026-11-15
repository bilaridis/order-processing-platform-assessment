# 05 Azure reference architecture

Every service is its own Azure Functions app on Windows App Service plans grouped by demand, connected
only by Service Bus ([ADR-0001](../adr/0001-microservices-and-function-per-feature.md),
[ADR-0009](../adr/0009-azure-hosting.md), [ADR-0010](../adr/0010-service-plans-by-demand.md)).

## Deployment view

```mermaid
flowchart TB
    clients([Web shop, mobile app, back office])
    external[[Providers: payment, carriers, inventory]]

    subgraph azure [Azure, one EU region]
        fd[Front Door + WAF]

        subgraph hot [Order intake plan: sized for the peak, a customer waits]
            api[Customer API]
            ordering[Ordering intake]
            pricing[Pricing]
            inventory[Inventory]
            offers[Carrier offers]
        end
        subgraph flow [Order flow plan: nobody waits, sized for the average]
            fulfilment[Ordering flow]
            payments[Ecommerce]
            shipping[Carrier shipments]
            notifications[Notifications]
            proj[Order API projector]
        end
        subgraph ondemand [On demand: shares the order flow plan at launch]
            backoffice[Back-office API]
            documents[Documents]
            opstool[Operations tool]
        end

        sb{{Service Bus<br/>queues for commands, topics for events}}
        sql[(Azure SQL<br/>Ordering)]
        tables[(Table storage<br/>per service, views)]
        blobs[(Blob storage)]
        kv[Key Vault]
        mon[Application Insights]
    end

    clients -- HTTPS --> fd
    external -- webhooks --> fd
    fd -- "/graphql" --> api
    fd -- "/backoffice/graphql" --> backoffice
    fd -- "/webhooks" --> payments & shipping & inventory
    api & backoffice -- commands --> sb
    sb <--> ordering & fulfilment & pricing & inventory & offers & payments & shipping & notifications & documents
    sb --> proj --> tables
    api & backoffice -- queries --> tables
    ordering & fulfilment --> sql
    payments & shipping & inventory -- "HTTPS, resilient" --> external
    clients -. "short-lived SAS link" .-> blobs
    documents --> blobs
    opstool -- "inspect, resubmit" --> sb
```

Inside a service, a function turns its trigger (a Service Bus message or a timer) into a command and
calls the handler; the handler uses the domain and the ports; Azure sits in adapters.

## Service mapping

| Concern | Azure service | Notes |
|---------|---------------|-------|
| Order API | **App Service**, two web apps from one codebase | Customer API on the intake plan, back-office API on the on-demand plan ([ADR-0013](../adr/0013-cqrs-app-service.md)) |
| Core, supporting and integration services | **Azure Functions** on Windows App Service plans | Service Bus and timer triggers; HTTP only for provider webhooks. Ordering is two apps (intake, flow). Staging slot per app |
| Plans | **App Service plans by demand** | Order intake alone; order flow and on-demand work share a second plan at launch |
| Messaging | **Service Bus Standard** | Queue per service for commands, topic per service for events, subscription per consumer and event, duplicate detection, dead-letter alert |
| Data of record | **Azure SQL Database** | Ordering only, with a geo-secondary in the paired region |
| Integration state, views | **Table storage** | Each service owns its tables, keyed by order id |
| Files | **Blob storage** | Private containers, a lobby, user delegation SAS links |
| Identity | **Entra External ID** (customers), **Entra ID** (staff), **managed identities** | No passwords or keys in configuration |
| Secrets | **Key Vault** | Provider keys, webhook secrets, offer signing key |
| Edge | **Front Door** with WAF | One public entry; the back-office route can be limited to the company network |
| Observability | **Application Insights** on Log Analytics | OpenTelemetry; one trace across API, Service Bus and functions |
| Infrastructure as code | **Terraform** | Reviewed and scanned plans, applied by the pipeline ([ADR-0011](../adr/0011-deployment.md)) |
| Pipeline | **GitHub Actions** with OIDC | One workflow per service: build, test, package, slot, swap |

## Message flow for an order

```mermaid
sequenceDiagram
    autonumber
    actor C as Client
    participant API as Customer API
    participant O as Ordering
    participant SB as Service Bus
    participant E as Ecommerce
    participant X as Payment provider

    C->>API: placeOrder
    API->>SB: PlaceOrder (answers Submitted)
    SB->>O: PlaceOrder
    O->>SB: OrderSubmitted, RequestQuote, then ReserveStock
    SB->>O: PriceQuoted, StockReserved
    O->>SB: OrderPlaced (Pending), AuthorizePayment
    SB->>E: AuthorizePayment
    E->>X: payment session (key = order id)
    X->>E: webhook: authorized
    E->>SB: PaymentAuthorized
    SB->>O: PaymentAuthorized
    O->>SB: OrderConfirmed, RequestShipment
```

Each handler saves its state and inbox entry in one transaction, then publishes; a failure in between
is repaired by redelivery ([ADR-0012](../adr/0012-reliable-messaging-without-an-outbox.md)).

## Data

**Azure SQL with controlled execution:** parameterised EF Core queries only; each managed identity is a
database user limited to its schema, no DDL at runtime; migrations are reviewed scripts run by the
pipeline; human write access is just-in-time through Entra and audited; auditing and Query Store on.

**Table storage:** references, inbox entries and last known statuses, keyed by order id so the writes
for one order are atomic. Not for reporting.

**Files** ([ADR-0004](../adr/0004-data-ownership-and-storage.md#4-files-one-upload-lobby-a-file-token-read-links-signed-by-the-order-api)):

1. **Upload.** The client gets a file token and a write-only SAS for `lobby/{token}` from the Order API.
2. **Lobby to container.** Event Grid reports the blob to Documents, which checks size and type, waits
   for the Defender malware scan, copies the file to its container, records it and publishes
   `DocumentStored`. The lobby empties after 24 hours.
3. **Download.** The Order API checks ownership of the token and returns a read-only user delegation
   SAS for one blob, valid for minutes. The API never streams files. Account keys are disabled.

## Networking: private only where required

| Step | What | Extra a month |
|------|------|---------------|
| **1. Launch** | Public endpoints, Entra only; apps reachable only through Front Door | none |
| **2. Private data** | Private endpoints for SQL, Storage, Key Vault | a few tens of euros |
| **3. Private messaging** | Service Bus Premium with a private endpoint | about €600 |
| **4. Private inbound** | Application Gateway with WAF | a few hundred euros |

Each step needs a named requirement ([cost forecast](../estimation/02-azure-cost-forecast.md#the-cost-of-going-private)).

## Environments and recovery

Test, staging and production, each with its own budget. One region for the apps; a geo-secondary of
the Ordering database in the paired region (recovery point 15 minutes); geo-redundant storage. The
environment is rebuilt by Terraform in 12 hours ([03](03-quality-attributes.md#targets)).

## From the skeleton to Azure

Domain and application code move unchanged. What changes is in hosts and infrastructure: a Functions
host per service, the Service Bus transport (already compiled in `OrderProcessing.BuildingBlocks.Azure`),
EF Core on Azure SQL, Table storage repositories, the Documents service, and the Azure Monitor
exporter and Entra validation by configuration.
