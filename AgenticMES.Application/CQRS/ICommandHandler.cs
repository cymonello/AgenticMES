namespace AgenticMES.Application.CQRS;

/// <summary>Handles a single CQRS command. Implementations live in the Application layer.</summary>
public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<TResponse> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}
