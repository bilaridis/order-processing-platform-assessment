using System.Security.Claims;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;
using OrderProcessing.Carriers.Contracts;
using OrderProcessing.OrderApi;
using OrderProcessing.Ordering.Contracts;

namespace OrderProcessing.Modules.UnitTests.OrderApi;

/// <summary>
/// The two GraphQL schemas of the Order API (ADR-0013): the customer API and the back-office API.
/// The schema files in docs/api are the contract; a change to the code that changes a schema fails here
/// until the file is updated on purpose.
/// </summary>
public sealed class GraphQLApiTests : IAsyncDisposable
{
    private const string PlaceOrder = """
        mutation {
          placeOrder(input: {
            currency: "EUR", items: [{ sku: "SKU-1", quantity: 2 }], paymentMethod: "card", idempotencyKey: "k-1",
            shippingAddress: { recipient: "Ada", line1: "1 Main St", city: "Athens", postalCode: "10431", country: "GR" } })
          { order { orderId status } error { code } }
        }
        """;

    private readonly FakeOrdering _ordering = new();
    private readonly FakeCarrierService _carriers = new();
    private readonly ServiceProvider _services;

    public GraphQLApiTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOrderingApi>(_ordering);
        services.AddSingleton<IDeliveryOffersApi>(_carriers);
        new OrderApiModule().Register(services, new ConfigurationBuilder().Build());
        _services = services.BuildServiceProvider();
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    [Theory]
    [InlineData(OrderApiModule.CustomerSchema, "customer.graphql")]
    [InlineData(OrderApiModule.BackOfficeSchema, "backoffice.graphql")]
    public async Task The_schema_is_the_one_published_in_the_docs(string schema, string file)
    {
        var executor = await _services.GetRequiredService<IRequestExecutorResolver>().GetRequestExecutorAsync(schema);
        var path = System.IO.Path.Combine(RepositoryRoot(), "docs", "api", file);

        // To change the contract on purpose: UPDATE_GRAPHQL_SCHEMA=1 dotnet test, then review the diff.
        if (Environment.GetEnvironmentVariable("UPDATE_GRAPHQL_SCHEMA") == "1")
        {
            await File.WriteAllTextAsync(path, Normalise(executor.Schema.ToString()) + "\n");
        }

        Assert.Equal(Normalise(await File.ReadAllTextAsync(path)), Normalise(executor.Schema.ToString()));
    }

    [Fact]
    public async Task Placing_an_order_answers_received_and_sends_the_command()
    {
        var result = await RunAsync(OrderApiModule.CustomerSchema, PlaceOrder, Customer("customer-1"));

        Assert.Contains("\"status\": \"Submitted\"", result, StringComparison.Ordinal);
        Assert.Contains("\"error\": null", result, StringComparison.Ordinal);
        var command = Assert.Single(_ordering.Placed);
        Assert.Equal("customer-1", command.CustomerId);
    }

    [Fact]
    public async Task A_wallet_token_is_optional_and_travels_with_the_command()
    {
        await RunAsync(OrderApiModule.CustomerSchema, PlaceOrder, Customer("customer-1"));
        await RunAsync(
            OrderApiModule.CustomerSchema,
            PlaceOrder
                .Replace("paymentMethod: \"card\"", "paymentMethod: \"wallet\", paymentMethodToken: \"wallet-token-1\"", StringComparison.Ordinal)
                .Replace("k-1", "k-2", StringComparison.Ordinal),
            Customer("customer-1"));

        Assert.Null(_ordering.Placed[0].PaymentMethodToken);
        Assert.Equal("wallet", _ordering.Placed[1].PaymentMethod);
        Assert.Equal("wallet-token-1", _ordering.Placed[1].PaymentMethodToken);
    }

    [Fact]
    public async Task A_request_that_cannot_be_an_order_is_answered_with_its_error_code_and_sends_nothing()
    {
        var result = await RunAsync(
            OrderApiModule.CustomerSchema, PlaceOrder.Replace("quantity: 2", "quantity: 0", StringComparison.Ordinal), Customer("customer-1"));

        Assert.Contains("order.invalid_quantity", result, StringComparison.Ordinal);
        Assert.Empty(_ordering.Placed);
    }

    [Fact]
    public async Task A_customer_reads_only_their_own_order_and_staff_read_any()
    {
        await RunAsync(OrderApiModule.CustomerSchema, PlaceOrder, Customer("customer-1"));
        var query = $$"""{ order(id: "{{_ordering.Placed[0].OrderId}}") { customerId } }""";

        var owner = await RunAsync(OrderApiModule.CustomerSchema, query, Customer("customer-1"));
        var stranger = await RunAsync(OrderApiModule.CustomerSchema, query, Customer("customer-2"));
        var staff = await RunAsync(OrderApiModule.BackOfficeSchema, query, Staff("agent-1"));

        Assert.Contains("customer-1", owner, StringComparison.Ordinal);
        Assert.Contains("\"order\": null", stranger, StringComparison.Ordinal);
        Assert.Contains("customer-1", staff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_customer_schema_has_no_way_to_ask_for_another_customers_orders()
    {
        var result = await RunAsync(
            OrderApiModule.CustomerSchema, """{ ordersOfCustomer(customerId: "customer-2") { orderId } }""", Customer("customer-1"));

        Assert.Contains("does not exist", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_without_the_write_scope_cannot_change_anything()
    {
        var result = await RunAsync(OrderApiModule.CustomerSchema, PlaceOrder, User("customer-1", "orders.read"));

        Assert.Contains("AUTH_NOT_AUTHORIZED", result, StringComparison.Ordinal);
        Assert.Empty(_ordering.Placed);
    }

    [Fact]
    public async Task Staff_cancel_on_behalf_of_a_customer_and_Ordering_is_told_so()
    {
        var orderId = Guid.NewGuid();

        var result = await RunAsync(
            OrderApiModule.BackOfficeSchema, $$"""mutation { cancelOrder(orderId: "{{orderId}}", reason: "customer phoned") { accepted } }""", Staff("agent-1"));

        Assert.Contains("\"accepted\": true", result, StringComparison.Ordinal);
        var request = Assert.Single(_ordering.Cancelled);
        Assert.True(request.CanAccessAnyOrder);
        Assert.Equal(orderId, request.OrderId);
    }

    [Fact]
    public async Task Delivery_offers_are_asked_through_the_Order_API_and_read_from_its_view()
    {
        const string Ask = """
            mutation {
              requestDeliveryOffers(input: { destinationCountry: "GR", postalCode: "10431", items: [{ sku: "SKU-1", quantity: 1 }] })
              { deliveryOffers { requestId status } error { code } }
            }
            """;

        var asked = await RunAsync(OrderApiModule.CustomerSchema, Ask, Customer("customer-1"));

        Assert.Contains("\"status\": \"Requested\"", asked, StringComparison.Ordinal);
        var request = Assert.Single(_carriers.Requested);
        Assert.Equal("customer-1", request.CustomerId);

        // The carrier service answers with an event; the projector writes it into the view.
        await using (var scope = _services.CreateAsyncScope())
        {
            var offer = new DeliveryOffer(
                "BoxNow", "Locker", DeliveryType.Locker, "L-1", "Locker 1", 1.90m, "EUR",
                new DateOnly(2026, 11, 17), DateTimeOffset.UtcNow.AddMinutes(30), "token-1");
            await scope.ServiceProvider.GetRequiredService<IIntegrationEventHandler<DeliveryOffersQuoted>>()
                .HandleAsync(new DeliveryOffersQuoted(request.RequestId, "customer-1", [offer]), CancellationToken.None);
        }

        var read = $$"""{ deliveryOffers(requestId: "{{request.RequestId}}") { status offers { carrier token } } }""";
        var owner = await RunAsync(OrderApiModule.CustomerSchema, read, Customer("customer-1"));
        var stranger = await RunAsync(OrderApiModule.CustomerSchema, read, Customer("customer-2"));

        Assert.Contains("\"status\": \"Ready\"", owner, StringComparison.Ordinal);
        Assert.Contains("token-1", owner, StringComparison.Ordinal);
        Assert.Contains("\"deliveryOffers\": null", stranger, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_schema_is_not_published_to_callers()
    {
        var result = await RunAsync(OrderApiModule.CustomerSchema, "{ __schema { types { name } } }", Customer("customer-1"));

        Assert.Contains("Introspection is not allowed", result, StringComparison.Ordinal);
    }

    private async Task<string> RunAsync(string schema, string document, ClaimsPrincipal user)
    {
        var executor = await _services.GetRequiredService<IRequestExecutorResolver>().GetRequestExecutorAsync(schema);
        await using var scope = _services.CreateAsyncScope();

        var result = await executor.ExecuteAsync(
            OperationRequestBuilder.New().SetDocument(document).SetServices(scope.ServiceProvider).SetUser(user).Build());

        return result.ToJson();
    }

    private static ClaimsPrincipal Customer(string id) => User(id, "orders.read orders.write");

    private static ClaimsPrincipal Staff(string id) => User(id, "orders.manage");

    private static ClaimsPrincipal User(string id, string scopes) =>
        new(new ClaimsIdentity([new Claim("sub", id), new Claim("scope", scopes)], "test"));

    private static string Normalise(string schema) => schema.ReplaceLineEndings("\n").Trim();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "OrderProcessing.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private sealed class FakeOrdering : IOrderingApi
    {
        public List<PlaceOrderRequest> Placed { get; } = [];

        public List<CancelOrderRequest> Cancelled { get; } = [];

        public Task RequestPlacementAsync(PlaceOrderRequest request, CancellationToken cancellationToken)
        {
            Placed.Add(request);
            return Task.CompletedTask;
        }

        public Task RequestCancellationAsync(CancelOrderRequest request, CancellationToken cancellationToken)
        {
            Cancelled.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCarrierService : IDeliveryOffersApi
    {
        public List<DeliveryOffersQuoteRequest> Requested { get; } = [];

        public Task RequestOffersAsync(DeliveryOffersQuoteRequest request, CancellationToken cancellationToken)
        {
            Requested.Add(request);
            return Task.CompletedTask;
        }

        public Result<VerifiedDeliveryOffer> Verify(string token) => throw new NotSupportedException();
    }
}
