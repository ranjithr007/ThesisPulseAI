namespace ThesisPulse.Shared.Infrastructure.Messaging;

public interface IDispatchTarget
{
    Task SendAsync(
        OutboxMessage message,
        CancellationToken cancellationToken = default);
}
