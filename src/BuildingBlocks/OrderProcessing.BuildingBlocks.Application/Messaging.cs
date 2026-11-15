using OrderProcessing.BuildingBlocks.Domain;

namespace OrderProcessing.BuildingBlocks.Application;

// Use cases are plain handler interfaces resolved from DI. There is deliberately no mediator library:
// endpoints depend on the handler they call, which keeps the call graph navigable (see ADR-0003).
// Cross-cutting behaviour (validation, logging, transactions) is added with decorators.

public interface ICommand<TResult>
    where TResult : Result;

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
    where TResult : Result
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface IQuery<TResult>
    where TResult : Result;

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
    where TResult : Result
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
