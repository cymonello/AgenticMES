namespace AgenticMES.Application.CQRS;

/// <summary>Marker for an immutable CQRS command that produces <typeparamref name="TResponse"/>.</summary>
public interface ICommand<TResponse>;
