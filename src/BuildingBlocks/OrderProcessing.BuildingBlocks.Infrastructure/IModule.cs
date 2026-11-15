using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OrderProcessing.BuildingBlocks.Infrastructure;

/// <summary>
/// The only thing the host knows about a module. Each module registers its own services and endpoints,
/// so adding a module is one line in the host and extracting one into its own service is a new host.
/// </summary>
public interface IModule
{
    string Name { get; }

    void Register(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
