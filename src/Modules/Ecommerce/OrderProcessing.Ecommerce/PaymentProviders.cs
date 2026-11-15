using System.Collections.Concurrent;

namespace OrderProcessing.Ecommerce;

/// <summary>
/// Port to one payment provider (ADR-0006). One adapter per provider. The adapter translates the
/// provider's API, decline codes and webhooks into ours. Card data never reaches it: it receives the
/// token the provider's own client-side SDK issued.
/// </summary>
internal interface IPaymentProvider
{
    /// <summary>Key of the provider in the registry and in the payment ledger.</summary>
    string Name { get; }

    /// <summary>The payment method this provider serves.</summary>
    string Method { get; }

    /// <summary>The provider must deduplicate on <paramref name="idempotencyKey"/>.</summary>
    Task<PaymentProviderResult> AuthorizeAsync(
        string idempotencyKey, decimal amount, string currency, string paymentMethod, string? paymentMethodToken, CancellationToken cancellationToken);

    Task ReverseAsync(string paymentReference, CancellationToken cancellationToken);
}

internal sealed record PaymentProviderResult(bool Approved, string? PaymentReference, string? DeclineReason);

/// <summary>
/// SKELETON PLACEHOLDER for a real provider adapter. Approves everything except tokens ending in "_declined".
/// </summary>
internal sealed class FakePaymentProvider(string name, string method) : IPaymentProvider
{
    public string Name => name;

    public string Method => method;

    public Task<PaymentProviderResult> AuthorizeAsync(
        string idempotencyKey, decimal amount, string currency, string paymentMethod, string? paymentMethodToken, CancellationToken cancellationToken) =>
        Task.FromResult(paymentMethod.EndsWith("_declined", StringComparison.Ordinal)
            ? new PaymentProviderResult(false, null, "Payment declined")
            : new PaymentProviderResult(true, $"{name}_{idempotencyKey}", null));

    public Task ReverseAsync(string paymentReference, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The two example providers of the skeleton: one for the method "card", one for "wallet". A suffix such as "card_declined" simulates a refusal.</summary>
internal static class ExampleProviders
{
    public const string Cards = "cards";
    public const string Wallet = "wallet";

    public static FakePaymentProvider CreateCards() => new(Cards, "card");

    public static FakePaymentProvider CreateWallet() => new(Wallet, "wallet");
}

/// <summary>
/// Chooses the provider by payment method. Unlike carriers, this is not a choice the customer makes
/// between competing offers: a payment method belongs to exactly one provider.
/// </summary>
internal sealed class PaymentProviderResolver(IEnumerable<IPaymentProvider> providers)
{
    private readonly List<IPaymentProvider> _providers = [.. providers];

    public IPaymentProvider? For(string paymentMethod) =>
        _providers.FirstOrDefault(provider => paymentMethod.StartsWith(provider.Method, StringComparison.Ordinal));

    public IPaymentProvider? Find(string name) =>
        _providers.FirstOrDefault(provider => string.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase));
}

internal sealed record PaymentRecord(string Provider, string Reference);

/// <summary>SKELETON PLACEHOLDER for the service's Payments table (PartitionKey = order id).</summary>
internal sealed class InMemoryPaymentLedger
{
    private readonly ConcurrentDictionary<Guid, PaymentRecord> _byOrder = new();

    public bool TryGet(Guid orderId, out PaymentRecord? payment) => _byOrder.TryGetValue(orderId, out payment);

    public void Record(Guid orderId, PaymentRecord payment) => _byOrder[orderId] = payment;

    public bool TryRemove(Guid orderId, out PaymentRecord? payment) => _byOrder.TryRemove(orderId, out payment);
}
