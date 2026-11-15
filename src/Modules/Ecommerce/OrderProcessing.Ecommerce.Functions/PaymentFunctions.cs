using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using OrderProcessing.BuildingBlocks.Azure;
using OrderProcessing.Ecommerce.Contracts;

namespace OrderProcessing.Ecommerce.Functions;

/// <summary>Function app "payments", on the order flow plan (ADR-0010).</summary>
public sealed class PaymentFunctions(IEcommerceApi payments)
{
    /// <summary>
    /// The commands of this service arrive on one queue, and a queue has one trigger. So the two features
    /// "authorise" and "reverse" share this function and are told apart by the message subject. Each still
    /// has its own handler. A subject nobody knows fails the message into the dead-letter queue.
    /// Both commands are idempotent per order (ADR-0012): the payment ledger is the inbox here, and the
    /// outcome is published from it, so a redelivered command publishes the same outcome again.
    /// </summary>
    [Function("OnPaymentCommand")]
    public Task OnPaymentCommandAsync(
        [ServiceBusTrigger(EcommerceMessaging.CommandQueue, Connection = "ServiceBus", IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message,
        CancellationToken cancellationToken) =>
        message.Subject switch
        {
            EcommerceMessaging.AuthorizePayment => payments.RequestAuthorizationAsync(
                ServiceBusMessages.Read<AuthorizePaymentRequest>(message), cancellationToken),
            EcommerceMessaging.ReversePayment => payments.RequestReversalAsync(
                ServiceBusMessages.Read<ReversePaymentRequest>(message).OrderId, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown payment command '{message.Subject}'."),
        };
}
