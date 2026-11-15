using OrderProcessing.Ecommerce;
using OrderProcessing.Ecommerce.Contracts;

namespace OrderProcessing.Modules.UnitTests.Ecommerce;

public class EcommerceApiTests
{
    private readonly RecordingPublisher _published = new();
    private readonly EcommerceApi _payments;

    public EcommerceApiTests()
    {
        _payments = new EcommerceApi(
            new PaymentProviderResolver([ExampleProviders.CreateCards(), ExampleProviders.CreateWallet()]),
            new InMemoryPaymentLedger(),
            _published);
    }

    [Theory]
    [InlineData("card", ExampleProviders.Cards)]
    [InlineData("wallet", ExampleProviders.Wallet)]
    public async Task The_payment_method_decides_the_provider(string token, string provider)
    {
        await _payments.RequestAuthorizationAsync(Request(token), CancellationToken.None);

        var authorized = Assert.IsType<PaymentAuthorized>(Assert.Single(_published.Events));
        Assert.StartsWith(provider, authorized.PaymentReference, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("card_declined")]
    [InlineData("crypto_ok")]
    public async Task A_decline_or_an_unsupported_method_fails_the_payment(string token)
    {
        await _payments.RequestAuthorizationAsync(Request(token), CancellationToken.None);

        Assert.IsType<PaymentFailed>(Assert.Single(_published.Events));
    }

    [Fact]
    public async Task Asking_twice_for_the_same_order_charges_once_and_reports_the_same_payment()
    {
        var request = Request("card");

        await _payments.RequestAuthorizationAsync(request, CancellationToken.None);
        await _payments.RequestAuthorizationAsync(request, CancellationToken.None);

        Assert.Single(_published.Events.Cast<PaymentAuthorized>().Select(authorized => authorized.PaymentReference).Distinct());
    }

    private static AuthorizePaymentRequest Request(string method) => new(Guid.NewGuid(), 24m, "EUR", method);
}
