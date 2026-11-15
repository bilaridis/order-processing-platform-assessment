using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using OrderProcessing.BuildingBlocks.Azure;

namespace OrderProcessing.Operations.Functions;

/// <summary>
/// Makes stuck orders visible and repairable for L1 support (ADR-0012, ADR-0008).
/// The counts per queue and subscription need no code: they are Service Bus metrics in Azure Monitor.
/// These functions add what the metrics cannot show: which order, which step and why.
/// </summary>
public sealed class DeadLetterFunctions(DeadLetterInspector inspector)
{
    public const string ResubmitQueue = "operations-resubmit";

    /// <summary>Every five minutes: one structured log line per new dead-lettered message.</summary>
    [Function("InspectDeadLetters")]
    public Task InspectAsync([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken cancellationToken) =>
        inspector.InspectAsync(cancellationToken);

    /// <summary>
    /// A person asks for a resubmit by putting {"entityPath": "...", "messageId": "..."} on this queue,
    /// from the runbook (Service Bus Explorer in the portal, or the az CLI). A message on a queue rather
    /// than an HTTP endpoint keeps the rule that the platform is driven by Service Bus, leaves an audit
    /// trail, and needs no public endpoint for an operator tool.
    /// </summary>
    [Function("OnResubmitRequest")]
    public Task OnResubmitRequestAsync(
        [ServiceBusTrigger(ResubmitQueue, Connection = "ServiceBus")] ServiceBusReceivedMessage message,
        CancellationToken cancellationToken)
    {
        var request = ServiceBusMessages.Read<ResubmitRequest>(message);
        return inspector.ResubmitAsync(request.EntityPath, request.MessageId, cancellationToken);
    }
}

public sealed record ResubmitRequest(string EntityPath, string MessageId);
