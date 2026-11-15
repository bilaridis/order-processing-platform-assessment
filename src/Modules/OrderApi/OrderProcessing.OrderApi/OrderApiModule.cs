using HotChocolate.Execution.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;
using OrderProcessing.BuildingBlocks.Infrastructure;
using OrderProcessing.Ordering.Contracts;

namespace OrderProcessing.OrderApi;

/// <summary>
/// The CQRS app service between the front-end apps and the core services (ADR-0013), and the only API
/// of the platform for clients. It is GraphQL: a mutation checks the request, sends a command and
/// answers "received"; a query reads the order views, which a projector builds from events. It owns no
/// business rules and knows the other services only through their contracts.
///
/// There are two schemas because there are two presentation apps, split by audience:
/// <list type="bullet">
/// <item><c>/graphql</c>: the customer API, for the web shop and the mobile app. Own orders only.</item>
/// <item><c>/backoffice/graphql</c>: the back-office API, for staff. Any order.</item>
/// </list>
/// In Azure each schema is its own web app with its own identity provider. The local host serves both.
/// </summary>
public sealed class OrderApiModule : IModule
{
    public const string CustomerSchema = "customer";
    public const string BackOfficeSchema = "backoffice";

    internal const string ReadScope = "orders.read";
    internal const string WriteScope = "orders.write";
    internal const string ManageScope = "orders.manage";

    public string Name => "OrderApi";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IOrderViewStore, InMemoryOrderViewStore>();
        services.AddIntegrationEventHandler<OrderViewProjector>();
        services.AddScoped<ICommandHandler<SubmitOrder, Result<OrderView>>, SubmitOrderHandler>();
        services.AddScoped<IQueryHandler<GetOrderView, Result<OrderView>>, GetOrderViewHandler>();
        services.AddScoped<IQueryHandler<ListMyOrders, Result<IReadOnlyList<OrderListItem>>>, ListMyOrdersHandler>();
        services.AddSingleton<IDeliveryOffersViewStore, InMemoryDeliveryOffersViewStore>();
        services.AddIntegrationEventHandler<DeliveryOffersProjector>();
        services.AddScoped<ICommandHandler<RequestDeliveryOffers, Result<DeliveryOffersView>>, RequestDeliveryOffersHandler>();
        services.AddScoped<IQueryHandler<GetDeliveryOffers, Result<DeliveryOffersView>>, GetDeliveryOffersHandler>();

        // A public GraphQL endpoint must not describe itself to strangers. On for local development only.
        var introspection = configuration.GetValue("GraphQL:Introspection", false);

        Harden(services.AddGraphQLServer(CustomerSchema)
            .AddQueryType<CustomerQueries>()
            .AddMutationType<CustomerMutations>()
            .AddInputObjectType<OrderSubmittedItem>(type => type.Name("OrderItemInput"))
            .AddInputObjectType<OrderSummaryDestination>(type => type.Name("AddressInput")), introspection);

        Harden(services.AddGraphQLServer(BackOfficeSchema)
            .AddQueryType<BackOfficeQueries>()
            .AddMutationType<BackOfficeMutations>(), introspection);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGraphQL("/graphql", CustomerSchema)
            .RequireAuthorization(policy => policy.RequireAuthenticatedUser().RequireScope(ReadScope, WriteScope));

        // Staff only. A customer token never has this scope, and in Azure never reaches this app at all.
        endpoints.MapGraphQL("/backoffice/graphql", BackOfficeSchema)
            .RequireAuthorization(policy => policy.RequireAuthenticatedUser().RequireScope(ManageScope));
    }

    // What REST gets from a fixed set of routes, GraphQL has to be given: a client chooses the query,
    // so its depth is limited (the cost limits of the server are on by default), and the schema is
    // not published outside development.
    private static void Harden(IRequestExecutorBuilder schema, bool introspection) =>
        schema
            // The schema is the contract of the front ends: its names are theirs, not the events'.
            .AddObjectType<OrderSubmittedItem>(type => type.Name("OrderItem"))
            .AddObjectType<OrderSummaryDestination>(type => type.Name("Address"))
            .AddMaxExecutionDepthRule(8)
            .DisableIntrospection(!introspection)
            .ModifyRequestOptions(options => options.IncludeExceptionDetails = false);
}
