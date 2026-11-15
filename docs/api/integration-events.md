# Integration events

Events are facts in the past tense, published by the module that owns the fact. They are the
asynchronous half of the module contracts ([ADR-0003](../adr/0003-event-driven-communication.md)).

## Delivery guarantees

- **At least once.** A consumer may see the same event twice. Deduplicate on `eventId` or be naturally idempotent.
- **No global ordering.** Events of one order are normally in order, but consumers must not depend on
  it. The `Order` aggregate ignores or refuses transitions that arrive late.
- **Published after the state is stored, by the handler itself.** There is no outbox
  ([ADR-0012](../adr/0012-reliable-messaging-without-an-outbox.md)). If publishing fails, the handler's
  own message is delivered again and it publishes again, so **the same fact can be published twice**.

## Envelope

Every event carries:

| Field | Type | Meaning |
|-------|------|---------|
| `eventId` | UUID | **Derived** from the order id, the event type and, where an order can have several events of the type, a discriminator (version, carrier status). The same fact always has the same id. Deduplication key. |
| `occurredAt` | timestamp (UTC) | When the fact happened. Not part of the id. |

On Azure Service Bus ([ADR-0003](../adr/0003-event-driven-communication.md)) `eventId` is the message id
(used for duplicate detection), the event type is the message subject used by subscription filters, the
order id is the session id, and the trace context travels as message properties.
Payloads stay small: identifiers and facts, never documents. **Personal data travels only in the
messages listed in the [data map](../architecture/07-data-protection-and-compliance.md#messages-that-carry-personal-data)**,
and every queue and subscription has a time to live ([ADR-0014](../adr/0014-personal-data-and-payment-regulation.md)).

## Commands

A request to a service is a message on its queue; the subject names the command and the answer is an
event. There is no HTTP between services.

| Queue | Subject | Sent by | Answer |
|-------|---------|---------|--------|
| `ordering-commands` | `PlaceOrder` | Order API | `OrderSubmitted`, then `OrderPlaced` or `OrderRejected` |
| `ordering-commands` | `CancelOrder` | Order API | `OrderCancelled` or `OrderCancellationRefused` |
| `pricing-commands` | `RequestQuote` | Ordering | `PriceQuoted` or `PriceQuoteRejected` |
| `inventory-commands` | `ReserveStock` | Ordering | `StockReserved` or `StockReservationFailed` |
| `inventory-commands` | `ReleaseStock` | Ordering | none |
| `payments-commands` | `AuthorizePayment` | Ordering | `PaymentActionRequired` (card: the customer must authenticate), then `PaymentAuthorized` or `PaymentFailed` |
| `payments-commands` | `ReversePayment` | Ordering | none today (`PaymentReversed` planned) |
| `shipping-commands` | `QuoteDeliveryOffers` (`requestId`, `customerId`, destination, items) | Order API, at checkout | `DeliveryOffersQuoted` |
| `shipping-commands` | `RequestShipment` | Ordering | `ShipmentFailed`, or later `ShipmentDispatched` |
| `shipping-commands` | `CancelShipment` | Ordering | none today (`ShipmentCancelled` planned) |
| `documents-commands` (planned) | `RegisterUpload` (`fileToken`, `kind`, `orderId`, `owner`, `maxBytes`) | Order API, or an internal service before it writes to the lobby | `DocumentStored` or `DocumentRejected` once the file arrives |

The message id of a command is derived from the order id and the command, so a repeated send is
dropped by duplicate detection, and every receiving handler is idempotent per order id anyway.

## Catalogue

### Published by Ordering (`OrderProcessing.Ordering.Contracts`)

One event per lifecycle transition. Consumers that only need to follow orders (notifications,
analytics, loyalty) subscribe here and never call Ordering.

| Event | Published when | Payload |
|-------|----------------|---------|
| `OrderSubmitted` | Order received (`Submitted`), not yet checked | `orderId`, `customerId`, `items` (sku, quantity), `destination`, `version`, `contact` (email, mobile, language; may be null) |
| `OrderPlaced` | Order accepted (`Pending`): priced and stock reserved | `orderId`, `customerId`, `total`, `currency`, `version`, `summary` (lines, charges, total, destination, time placed) |
| `OrderConfirmed` | Payment authorized (`Confirmed`) | `orderId`, `version` |
| `OrderRejected` | Platform could not complete the order | `orderId`, `reason`, `version` |
| `OrderCancelled` | Cancelled on request | `orderId`, `reason`, `version` |
| `OrderCancellationRefused` | A cancel request could not be honoured (already shipped, not the caller's order). The order did not change | `orderId`, `code`, `reason` |
| `OrderShipped` | Handed to the carrier | `orderId`, `carrier`, `trackingNumber`, `version` |
| `OrderDelivered` | Delivered | `orderId`, `version` |

`version` is the position of the transition in the order's history (1 = submitted, 2 = accepted or rejected, and so on). A consumer that keeps a
copy of the status ignores an event whose version is not higher than the one it has
([ADR-0013](../adr/0013-cqrs-app-service.md)).

### Published by Ecommerce (`OrderProcessing.Ecommerce.Contracts`)

| Event | Published when | Payload |
|-------|----------------|---------|
| `PaymentAuthorized` | Provider approved the payment | `orderId`, `paymentReference` |
| `PaymentFailed` | Provider declined, authentication failed, or the attempt expired | `orderId`, `reason` |

### Published by Carriers (`OrderProcessing.Carriers.Contracts`)

| Event | Published when | Payload |
|-------|----------------|---------|
| `ShipmentDispatched` | Carrier collected the parcel | `orderId`, `carrier`, `trackingNumber` |
| `ShipmentDelivered` | Carrier confirmed delivery | `orderId` |
| `ShipmentFailed` | The chosen carrier refused the shipment. No other carrier is substituted | `orderId`, `carrier`, `reason` |
| `DeliveryOffersQuoted` | The carriers answered a `QuoteDeliveryOffers`. Event id derived from the request id | `requestId`, `customerId`, `offers` (each with a signed token) |
| `ShipmentTrackingUpdated` | The carrier reported progress that is not a status of the order: in transit, out for delivery, ready for collection | `orderId`, `carrier`, `status`, `location` |

### Published by Pricing (`OrderProcessing.Pricing.Contracts`)

| Event | Published when | Payload |
|-------|----------------|---------|
| `PriceQuoted` | The basket was priced | `orderId`, `quote` (lines, charges, total, currency) |
| `PriceQuoteRejected` | It could not be priced: unknown product, invalid or expired delivery offer | `orderId`, `code`, `reason` |

### Published by Inventory (`OrderProcessing.Inventory.Contracts`)

| Event | Published when | Payload |
|-------|----------------|---------|
| `StockReserved` | Every item of the order is held | `orderId`, `source` |
| `StockReservationFailed` | Stock is short, or the source could not answer | `orderId`, `code` (`inventory.insufficient_stock`, `inventory.unavailable`), `shortages` |

### Published by the notification service (`OrderProcessing.Notifications.Contracts`, topic `notifications-events`)

| Event | Published when | Payload |
|-------|----------------|---------|
| `CustomerNotified` | The provider reported the message delivered (email, SMS) or accepted it (push) | `orderId`, `channel`, `trigger` |
| `NotificationFailed` | The message was refused or reported undeliverable; it is not retried | `orderId`, `channel`, `trigger`, `reason` |

The id of both is derived from order, channel and trigger, so a repeated delivery report is one event.

### Planned additions

Designed in [06 Integration services](../architecture/06-integration-services.md), not in the skeleton yet.
All are additive.

| Change | Why |
|--------|-----|
| `OrderPlaced` gains `allocations` (source per item) and `deliveryOffer` (carrier, service, price) | The carrier service needs the origin and the offer the customer chose |
| `ShipmentCreated` (`orderId`, `carrier`, `trackingNumber`, `labelReference`) | The chosen carrier accepted the booking |
| `ShipmentCancelled` | A booked shipment was cancelled |
| `PaymentReversed` (`orderId`, `paymentReference`) | A void or refund was completed |
| `DocumentStored` (`fileToken`, `kind`, `orderId`, `owner`, `container`, `path`, `contentType`) and `DocumentRejected` (`fileToken`, `reason`) from Documents | A file left the lobby, or was refused ([ADR-0004](../adr/0004-data-ownership-and-storage.md)) |
| `OrderDisputeOpened` (`orderId`, `source` carrier or customer, `reason`, `version`) and `OrderDisputeResolved` (`orderId`, `outcome` refund or no_refund, `note`, `version`) from Ordering | A complaint the company settles by hand; the outcome is the company's ([02](../architecture/02-order-lifecycle.md#disputes)) |
| `PaymentActionRequired` (`orderId`, `redirectUrl`, `expiresAt`) | Strong customer authentication: the customer pays on the provider's hosted page ([ADR-0014](../adr/0014-personal-data-and-payment-regulation.md)) |
| `EraseCustomerData` command on `ordering-commands`; `CustomerDataErasureRequested` (`customerId`, `orderIds`) from Ordering; `CustomerDataErased` (`service`, `customerId`) from every service that holds personal data | GDPR erasure as a workflow ([07](../architecture/07-data-protection-and-compliance.md#erasure-across-services)) |

## Who consumes what

| Event | Consumer | Reaction |
|-------|----------|----------|
| `OrderSubmitted` | Ordering (process manager) | Request a quote from Pricing |
| `PriceQuoted` | Ordering | Store the quote on the order, request the stock reservation |
| `PriceQuoteRejected` | Ordering | Reject the order |
| `StockReserved` | Ordering | Accept the order (`Pending`); release the stock if the order was cancelled or rejected meanwhile |
| `StockReservationFailed` | Ordering | Reject the order |
| `OrderPlaced` | Ordering (process manager) | Request payment authorization |
| `PaymentAuthorized` | Ordering | Confirm the order; reverse the payment if the order was cancelled meanwhile |
| `PaymentFailed` | Ordering | Reject the order |
| `OrderConfirmed` | Ordering | Request shipment |
| `ShipmentDispatched` | Ordering | Mark shipped |
| `ShipmentDelivered` | Ordering | Mark delivered |
| `ShipmentFailed` | Ordering | Open a dispute on the order and publish `OrderDisputeOpened`. Staff settle it ([02](../architecture/02-order-lifecycle.md#disputes)) |
| `OrderDisputeOpened`, `OrderDisputeResolved` | Order API (projector), Notifications | Show the dispute in the back office's list and the order's timeline; tell the customer it was received, then its result |
| `OrderDisputeResolved` with outcome `refund` | Ordering | Reverse the payment; if not shipped, cancel the order and release the stock |
| `OrderRejected` | Ordering | Release stock |
| `OrderCancelled` | Ordering | Cancel shipment, reverse payment, release stock |
| `OrderSubmitted`, `OrderPlaced`, `OrderRejected`, `OrderShipped`, `OrderDelivered`, `OrderCancelled`, `ShipmentTrackingUpdated` | notification-integration-service | Store the contact details; notify the customer by the channels the policy table gives for that event |
| `DeliveryOffersQuoted` | Order API (projector) | Write the offers into the customer's delivery offers view, status `Ready` |
| `CustomerNotified`, `NotificationFailed` | Order API (projector) | Add a line to the order's timeline, for support |
| Every Ordering event, `PaymentAuthorized`, `PaymentFailed`, `ShipmentTrackingUpdated`, `ShipmentFailed` | Order API (projector) | Update the order view. It writes nothing else and never changes an order |

Ordering does not consume `ShipmentTrackingUpdated`: carrier detail goes straight into the view.

Ordering consumes its own events on purpose: each step of the process starts from a stored fact and
from a message, so the Service Bus trigger retries it on its own if it fails. That is what makes it
safe to have no outbox.

## Evolving events

- Add fields; never remove or repurpose one. Consumers ignore fields they do not know.
- A change in meaning is a new event type (`OrderShippedV2`), published alongside the old one until
  consumers have moved.
- Keep payloads small: identifiers and the facts of the change. Consumers that need more read the order.
- No personal data in events beyond identifiers, with one recorded exception: `OrderSubmitted` and the
  `summary` of `OrderPlaced` carry the destination, so that the Order API can build its view without calling
  Ordering. Access to the topic is limited to named identities, and retention and erasure rules apply
  to the views ([ADR-0013](../adr/0013-cqrs-app-service.md)).
