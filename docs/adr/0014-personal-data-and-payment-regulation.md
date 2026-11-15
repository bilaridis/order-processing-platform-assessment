# ADR-0014: Personal data and payment regulation: GDPR, PSD2 strong customer authentication, PCI DSS

- Date: 2026-11-14

## Context

The platform sells to consumers in Greece (and possibly the rest of the EU), so three bodies of rules
shape the design, and a fourth shapes the documents:

- **GDPR** (Regulation (EU) 2016/679). The platform processes personal data in almost every service:
  names, delivery addresses, email and mobile numbers, purchase history, photos taken at delivery.
- **PSD2** (Directive (EU) 2015/2366) and its technical standard on **strong customer authentication**
  (SCA, Delegated Regulation (EU) 2018/389). Card payments by EEA cardholders need two factors from the
  cardholder, in practice through **3-D Secure 2**, unless the provider obtains an exemption.
- **PCI DSS** (version 4.0.1), a card-scheme contract, not a law: whoever stores, processes or transmits
  card numbers, or can affect the page where they are entered, is in scope.
- **Greek tax law**: invoices and receipts are transmitted to AADE's **myDATA**, and must be kept for
  the statutory period.

Three facts about our own design make this more than a checklist:

1. The order is answered "received" and paid **later, in the background**
   ([ADR-0005](0005-order-placement-flow.md)). Under SCA the cardholder must be **present** when the
   payment is authenticated, and the background step has no customer in front of it.
2. Personal data travels **in Service Bus messages** and is copied into **views and tables of several
   services** ([ADR-0004](0004-data-ownership-and-storage.md), [ADR-0013](0013-cqrs-app-service.md)).
   "Delete the customer" is not one `DELETE` statement.
3. Personal data leaves the platform to **processors and other controllers**: the payment provider,
   carriers, the 3PL, Azure Communication Services, and through Notification Hubs to Apple and Google.

**What PSD2 does and does not ask of us.** PSD2 regulates payment service providers and the issuing
banks, not a shop. The platform has no PSD2 licence obligation as long as it **never holds money on
behalf of someone else**: it sells its own goods and the provider settles to its own account. The
moment partner sellers are paid out through the platform (a marketplace), that changes and is a legal
question, not a design one. Our obligation is practical: **a checkout that cannot run SCA is declined
by the issuer**, and a payment authenticated with SCA moves fraud liability to the issuer.

## Decision

### 1. Payment: a hosted payment step after acceptance, never card data on the platform

- The `placeOrder` mutation carries the **payment method** (card, wallet, bank transfer, pay on
  delivery). For a new card nothing else is collected before the order is accepted.
- It may also carry an optional **`paymentMethodToken`**: an opaque token from the provider or from a
  wallet. A token is not card data, because it holds no card number, account number or wallet id, so it
  does not change the PCI scope. It is for the two cases where a token is the right tool: a **wallet**
  (Apple Pay, Google Pay), where the customer was authenticated on the device, and a **saved payment
  method**. Ecommerce hands it to the provider with the final amount.
- **A token never replaces the payment step below.** If the provider answers that authentication is
  required, the flow is the same as for a new card: `PaymentActionRequired` and the hosted page.
- When the order becomes `Pending` (priced, stock reserved), Ordering sends `AuthorizePayment` with the
  **final amount**. Ecommerce creates a **payment session** with the provider for exactly that amount
  and publishes **`PaymentActionRequired`** with the session's redirect URL and its expiry.
- The Order API's projector puts the redirect into the order view (`payment.action`). The front end,
  which is already reading the view after the "received" answer, sends the customer to the provider's **hosted
  payment page**. The page collects the card and runs 3-D Secure 2 (frictionless or challenge).
- The provider's **signed webhook** (or, if it never arrives, the reconciliation timer) produces
  `PaymentAuthorized` or `PaymentFailed`. The customer's return to our site **decides nothing**: the
  return page only reads the view.
- If the provider authorizes without asking the customer anything (frictionless, or an exemption it
  applied), `PaymentAuthorized` follows directly and no redirect is shown.
- If the customer has gone (the order sat in `Submitted` while a service was down), the notification
  service sends a **"complete your payment" link**. The Order API asks Ecommerce for a new session; it
  never re-uses an expired one. The `Pending` timeout ([02](../architecture/02-order-lifecycle.md))
  ends an order that is never paid and releases the stock.
- Pay on delivery and bank transfer have no SCA step; they are a different adapter behind the same port.
- **The amount authenticated is the amount accepted.** SCA binds the authentication to the amount and
  the payee ("dynamic linking"). Because the price is final at `Pending`, the session is created for
  that price. A later increase (a charge added after acceptance) needs a new authentication or must not happen.
- **Capture** on dispatch is allowed (an authorization is valid for days, not weeks). Charging the customer
  again later without them present (merchant-initiated) needs a mandate agreed with SCA at checkout;
  it is out of scope.

**PCI DSS scope.** Card numbers never reach the platform, its logs or its queues. With a redirect to
the hosted page (or the provider's own iframe fields), the merchant qualifies for the lightest
self-assessment, **SAQ A**. Version 4.0.1 still asks us to protect the page that redirects or embeds:
a strict Content Security Policy, an inventory of scripts on the checkout page, and change detection on
it. The front end owns that, and it is a launch criterion. A page that posts card data through our own
JavaScript (SAQ A-EP) is not allowed.

### 2. Personal data: a map, minimisation in messages, retention per store, erasure by message

**The data map is part of the design.** [07 Data protection and compliance](../architecture/07-data-protection-and-compliance.md)
lists every store and message that holds personal data, its purpose, its lawful basis and its retention.
A pull request that adds a personal-data field to a contract, table or view updates the map, or it is
not merged.

**Rules for messages.** Service Bus is a transport, not a store, and is kept that way:

- A message carries personal data **only when a named consumer needs it to do its job**, and the
  consumers of that topic are listed in the map. Examples: the address in `RequestShipment` (the carrier
  needs it), the contact details for the notification service. Broadcast facts (`OrderConfirmed`,
  `OrderShipped`) carry ids, not people.
- **Time to live on every queue and subscription** (7 days), so a message nobody reads does not become
  an archive.
- **Dead-letter queues are purged** after the incident they belong to is closed, and at the latest after
  14 days. A dead-lettered message is an unplanned copy of personal data.
- No event store and no message archive. Because the platform does not keep events, there is no
  immutable log to erase from, and crypto-shredding is not needed.

**Rules for telemetry.** Logs, traces and metrics carry **ids, never names, addresses, emails, phone
numbers, tokens or SAS URLs**. Logging uses structured templates with a redaction enricher, and a test
fails on a known field logged in clear. Application Insights keeps data 90 days.

**Retention.** Each store has a period, enforced by a scheduled purge or a lifecycle rule, not by hope:

| Data | Kept for | Why |
|------|----------|-----|
| Order, its lines and price; invoice and receipt | The tax retention period (to be confirmed by the accountant; plan for 5 years after the end of the fiscal year, longer if an audit is open) | Legal obligation, GDPR Art. 6(1)(c) and 17(3)(b) |
| Delivery address and contact details on the order, the order view, notification contacts | Until delivery plus the return and complaint window (proposed 2 years), then removed from the order and deleted elsewhere | Contract; consumer claims |
| Proof-of-delivery photos | 90 days after delivery | Disputes about delivery |
| Payment attempts (references, outcome, no card data) | As the order | Disputes, chargebacks, accounting |
| Messages, dead letters, telemetry | 7 days, 14 days, 90 days | Operation only |
| Backups (Azure SQL point-in-time 7 days, long-term only if required) | As configured | Recovery |

**Erasure is a workflow over Service Bus, like everything else.** A verified request from the customer
(or from support) becomes `EraseCustomerData` on the Ordering queue. Ordering:

1. removes the personal data it is not obliged to keep (contact, address after delivery and the claim
   window) and **pseudonymises** what tax law makes it keep;
2. publishes `CustomerDataErasureRequested` (customer id and order ids only);
3. every service that holds personal data subscribes, erases its copy, and publishes `CustomerDataErased`;
4. Ordering records completion per service, and raises an alert if the request is not complete within
   the 30 days that GDPR allows.

The order itself is not deleted while the tax period runs: **the right to erasure does not override a
legal obligation to keep the invoice.** The customer is told what was kept and why.

**Backups are not rewritten.** Erased data leaves the backups when they expire. If a backup is restored,
the erasure log is replayed before the database is used.

**Access and portability.** The customer's orders are already readable through the Order API; an export
function in the back office assembles the rest (contact copies, notification history) from each service
by the same message pattern.

### 3. Processors and where the data lives

- **All Azure resources in EU regions** (West Europe for production, as in the
  [cost forecast](../estimation/02-azure-cost-forecast.md)). Entra External ID tenant and Azure
  Communication Services resource created with **Europe** as their data location; these cannot be moved later.
- **A data processing agreement with every processor**, and a register of them in the map. Carriers and
  the payment provider may be **independent controllers** for parts of what they do (a carrier for its
  own delivery records, a provider for fraud screening); the analysis phase confirms the role per party.
- **Push notifications go through Apple and Google** (outside the EU). Push content is minimal: no
  address, no product names, only "your order has shipped" and a link into the app.
- **Naming:** "ACS" means Azure Communication Services in this repository. The courier ACS is always
  written **ACS Courier**.

### 4. Greek e-invoicing (myDATA): noted, not designed

Every sale needs an invoice or retail receipt transmitted to **myDATA**, carrying the MARK number and
QR code that AADE returns, and Greek B2B e-invoicing through a certified provider is being made
mandatory in phases during 2026. **The organisation's ERP issues the document**, from the `OrderShipped`
event, when the payment is captured. If the ERP cannot, the Documents service issues it through a
certified e-invoicing provider, one more adapter behind a port; the estimate prices that as escalation E17.

## Alternatives considered

**A token for every payment, authorized in the background, with no payment step**. The customer gets one step instead of two. The token itself is harmless, and it is kept as an
option for wallets and saved methods. Rejected as the only path: with SCA the issuer answers a background
authorization of a customer-initiated payment with a soft decline ("authentication required"), and the
background step has no way to show the customer a 3-D Secure challenge. Most EEA card payments would
fail or fall back to an unauthenticated payment with the fraud liability on us.

**Pay first, then place the order.** The customer pays before anything is checked. Rejected: the
amount is not final until Pricing answers, SCA binds the amount, and every out-of-stock order becomes
a refund. It also puts the payment back on the request path that [ADR-0005](0005-order-placement-flow.md) removed.

**Wait for acceptance inside the checkout request, then show the payment page.** Same customer
experience in the normal case. Not needed: the front end already polls the view after the "received" answer, and
`payment.action` appears there within a second when the order is accepted.

**Our own 3-D Secure server or card fields (SAQ A-EP or SAQ D).** More control over the page. Rejected:
it puts the platform in PCI scope and buys nothing a hosted page does not give.

**Crypto-shredding** (encrypt each customer's data with their own key, delete the key to erase).
Right when personal data sits in an immutable log. Rejected: we keep no event log, messages expire,
and every store can delete or overwrite a row. It would add a key per customer and a key service for nothing.

**Delete the whole order on request.** Simplest to explain. Rejected: unlawful while the tax period
runs, and it breaks accounting and chargeback handling.

## Consequences

- **The checkout has one more step**: "we are checking your order", then the provider's payment page.
  This is the usual e-commerce flow (place the order, then pay), and it is the price of answering before checking
  design meeting SCA. The time to acceptance must be short, or the customer waits twice; it is measured
  in staging ([E15](../estimation/01-high-level-estimate.md#cost-escalation-what-would-make-delivery-cost-more)).
- A `Pending` order can wait for the customer, not only for the provider. The `Pending` timeout must be
  long enough for a 3-D Secure challenge and the "complete your payment" link (proposed 30 minutes for
  cards, longer for bank transfer), and stock is held for that time.
- New contract: `PaymentActionRequired` event, `payment.action` in the view, `paymentMethod` and the
  optional `paymentMethodToken` on the `placeOrder` mutation (never card data), the erasure command and
  events. The schema already has both fields. The skeleton still authorizes in the background with a fake provider; the
  hosted payment step is in package 11b.
- Every service that keeps personal data has an erasure handler and a purge timer. That is one more
  function per service, and the erasure test runs across services in system testing.
- Personal data in a message must be justified in review. This slows down adding a field, on purpose.
- The platform is SAQ A, not "PCI certified", and is "SCA-ready", not "PSD2 compliant": those are the
  provider's words to use, not ours.
- Legal review (data protection officer, accountant) is needed during analysis for the retention
  periods, controller/processor roles and myDATA. This ADR gives the engineering defaults until then.
