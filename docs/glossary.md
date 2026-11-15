# Glossary

Terms and acronyms used in the documents, in the meaning they have here.

| Term | Meaning |
|------|---------|
| **3PL** | Third-party logistics. An outside company that stores stock in its warehouse, picks and packs orders and hands them to a carrier. Here it is one kind of inventory source. |
| **3-D Secure (3DS)** | The step in which the card issuer asks the customer to confirm a payment, usually in their banking app. It is how card payments meet SCA. |
| **ACS** | Two different things. In the notification service: Azure Communication Services (email and SMS). In the carrier examples: ACS Courier, a Greek carrier. The documents say which one. |
| **ADR** | Architecture decision record. One decision with its context, alternatives and consequences. |
| **Aggregate** | A group of objects changed together through one entry point. The `Order` with its items, lines and history is the aggregate here. |
| **At-least-once delivery** | A message is delivered until its handler succeeds, so it can arrive more than once. Handlers must be idempotent. |
| **BFF** | Backend for frontend. An API built for one client application. Considered and not chosen in ADR-0013. |
| **Command** | A message that asks one service to do something. It goes to that service's queue. |
| **Compensation** | The action that undoes an earlier step after a later one failed, for example releasing stock when payment fails. |
| **Consolidation** | Bringing the products of one order from several inventories to one warehouse and sending one parcel. Not at launch (06). |
| **CQRS** | Command query responsibility segregation. Writes (commands) and reads (queries) use separate models. Here: mutations queue commands, queries read order views. |
| **Dead-letter queue (DLQ)** | Where Service Bus puts a message that could not be handled after the allowed attempts. A person must look at it. |
| **Dispute** | Here: a complaint about an order, opened by the platform (the carrier refused the parcel) or by staff when the customer writes. The company settles it, with or without a refund. Not a card chargeback, which the payment provider calls a dispute too. |
| **Demand tier** | A group of apps with the same kind of load that share one App Service plan: order intake, order flow, on demand (ADR-0010). |
| **Entra ID, Entra External ID** | Microsoft's identity services, for staff and for customers respectively. |
| **Event** | A message that states a fact that already happened, for example `OrderPlaced`. It goes to the publisher's topic and any service may subscribe. |
| **File lobby** | The one blob container every uploaded file enters. Documents checks and scans a file there before moving it to its final container (ADR-0004). |
| **File token** | A random GUID that names a file for the front ends. It is an identifier, not a permission: the Order API checks ownership before it returns a read link. |
| **GDPR** | The EU General Data Protection Regulation. |
| **GraphQL** | An API style in which the client names the fields it wants. A query reads, a mutation changes. |
| **IaC** | Infrastructure as code. Here Terraform (ADR-0011). |
| **Idempotent** | Doing it twice has the same result as doing it once. |
| **Inbox** | A table of message ids already handled, so a repeated message is ignored. |
| **Integration service** | A service that puts one contract in front of several outside parties: payments, inventory, carriers, notifications. |
| **Item / line** | An item is what the customer asked for (product and quantity). A line is the same after pricing (name, unit price, total). |
| **L1 support** | First-line support: the people who receive the alerts and use the operations tool. |
| **myDATA** | The Greek tax authority's platform to which invoices and receipts are reported. |
| **NFR** | Non-functional requirement: how well the system must work rather than what it does, for example scalability, reliability, security or observability. The brief lists nine; [03](architecture/03-quality-attributes.md) says how each is met and the targets for each. |
| **OIDC** | OpenID Connect. Used for sign-in tokens and for GitHub Actions to reach Azure without a stored secret. |
| **Outbox** | A table in which a service stores messages to send in the same transaction as its data. Not used here (ADR-0012). |
| **PCI DSS, SAQ A** | The card industry's security standard, and its shortest self-assessment, which applies when card data never touches our systems. |
| **Persisted query** | A GraphQL query registered in advance and sent by id, so the server runs only known queries. |
| **Port, adapter** | A port is an interface the core defines. An adapter implements it for one outside party. |
| **Private endpoint** | A private network address for an Azure service, so traffic does not use its public address. |
| **Process manager, saga** | The code that drives a multi-step process by reacting to events and sending the next command. Here `OrderFulfillmentProcess` in Ordering. |
| **Projector** | The function that listens to events and updates the order views. |
| **PSD2, SCA** | The EU payment services directive, and the strong customer authentication it requires for card payments. |
| **RPO, RTO** | Recovery point objective: how much data may be lost. Recovery time objective: how long recovery may take. |
| **SAS link** | A time-limited signed link to one file in Blob storage. |
| **Session (Service Bus)** | A way to deliver the messages of one order in order, one at a time. The session id is the order id. |
| **SKU** | Stock keeping unit: the identifier of a product. Also used by Azure for a pricing tier. |
| **SLA, SLO** | Service level agreement: what a provider promises. Service level objective: what we aim for. |
| **Topic, subscription** | A Service Bus topic receives events. Each consumer has its own subscription with a filter. |
| **WAF** | Web application firewall, here on Azure Front Door. |
