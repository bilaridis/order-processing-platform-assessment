using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.BuildingBlocks.Application;

namespace OrderProcessing.BuildingBlocks.Azure;

/// <summary>
/// Naming and format of messages on Azure Service Bus (ADR-0003): a topic per publisher, the message
/// subject is the type name (subscriptions filter on it), the message id is the event id (duplicate
/// detection and the inbox use it), and the session is the order id (keeps the order of one order's messages).
/// </summary>
public static class ServiceBusMessages
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>"OrderProcessing.Ordering.Contracts.OrderPlaced" is published to "ordering-events".</summary>
    public static string TopicFor(Type eventType)
    {
        var parts = eventType.Namespace?.Split('.') ?? [];

        return parts is ["OrderProcessing", var service, "Contracts", ..]
            ? $"{service.ToLowerInvariant()}-events"
            : throw new InvalidOperationException($"{eventType} is not an integration event of a service's Contracts.");
    }

    public static ServiceBusMessage Create(object payload, string subject, string messageId)
    {
        var message = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), Json))
        {
            Subject = subject,
            MessageId = messageId,
            ContentType = "application/json",
        };

        // Every message of the platform is about one order.
        if (payload.GetType().GetProperty("OrderId")?.GetValue(payload) is Guid orderId)
        {
            message.SessionId = orderId.ToString("N");
        }

        // The trace continues in the consumer (ADR-0008).
        if (Activity.Current?.Id is { } traceParent)
        {
            message.ApplicationProperties["traceparent"] = traceParent;
        }

        return message;
    }

    public static T Read<T>(ServiceBusReceivedMessage message) =>
        message.Body.ToObjectFromJson<T>(Json)
        ?? throw new InvalidOperationException($"Message {message.MessageId} has no {typeof(T).Name} body.");
}

/// <summary>
/// Publishes an event to the topic of the service that owns it, from inside the handler (ADR-0012).
/// A failure to send throws, which fails the function and makes Service Bus deliver its message again.
/// </summary>
internal sealed class ServiceBusEventPublisher(ServiceBusSenders senders) : IEventPublisher
{
    public Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        var type = integrationEvent.GetType();

        return senders
            .For(ServiceBusMessages.TopicFor(type))
            .SendMessageAsync(
                ServiceBusMessages.Create(integrationEvent, type.Name, integrationEvent.EventId.ToString()), cancellationToken);
    }
}

/// <summary>
/// Sends a command to the queue of the service that owns it, from inside the handler. Like an event, a
/// failed send fails the function, and the redelivered message sends it again (ADR-0012).
/// </summary>
public sealed class ServiceBusCommandSender
{
    private readonly ServiceBusSenders _senders;

    internal ServiceBusCommandSender(ServiceBusSenders senders) => _senders = senders;

    /// <param name="messageId">Stable per command, so that a retry is removed by duplicate detection.</param>
    public Task SendAsync(string queue, string subject, object command, string messageId, CancellationToken cancellationToken) =>
        _senders.For(queue).SendMessageAsync(ServiceBusMessages.Create(command, subject, messageId), cancellationToken);
}

internal sealed class ServiceBusSenders(ServiceBusClient client)
{
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();

    public ServiceBusSender For(string queueOrTopic) => _senders.GetOrAdd(queueOrTopic, client.CreateSender);
}

public static class AzureMessagingExtensions
{
    /// <summary>
    /// Connects with the app's managed identity (ADR-0007): the setting
    /// <c>ServiceBus:fullyQualifiedNamespace</c> names the namespace and there is no connection string.
    /// The Service Bus triggers read the same setting.
    /// </summary>
    /// <summary>
    /// The dead-letter inspector and resubmit tool. Its identity needs the Service Bus Data Owner role,
    /// because listing entities and receiving from every dead-letter queue is more than a service may do.
    /// Only the operations function app registers it.
    /// </summary>
    public static IServiceCollection AddDeadLetterInspector(this IServiceCollection services, IConfiguration configuration)
    {
        var serviceBusNamespace = configuration["ServiceBus:fullyQualifiedNamespace"]
            ?? throw new InvalidOperationException("The setting ServiceBus:fullyQualifiedNamespace is missing.");
        services.AddSingleton(_ => new global::Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient(
            serviceBusNamespace, new DefaultAzureCredential()));
        services.AddSingleton<DeadLetterInspector>();
        return services;
    }

    public static IServiceCollection AddAzureServiceBusMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ =>
        {
            var serviceBusNamespace = configuration["ServiceBus:fullyQualifiedNamespace"]
                ?? throw new InvalidOperationException("The setting ServiceBus:fullyQualifiedNamespace is missing.");

            return new ServiceBusClient(serviceBusNamespace, new DefaultAzureCredential());
        });
        services.AddSingleton<ServiceBusSenders>();
        services.AddSingleton<IEventPublisher, ServiceBusEventPublisher>();
        services.AddSingleton(provider => new ServiceBusCommandSender(provider.GetRequiredService<ServiceBusSenders>()));
        return services;
    }
}
