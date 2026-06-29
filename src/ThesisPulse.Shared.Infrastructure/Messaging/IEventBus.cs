using ThesisPulse.Shared.Contracts.Messaging.V1;

namespace ThesisPulse.Shared.Infrastructure.Messaging;

public interface IEventBus
{
    IDisposable Subscribe<TPayload>(
        Func<EventEnvelope<TPayload>, CancellationToken, Task> handler)
        where TPayload : class;

    Task PublishAsync<TPayload>(
        EventEnvelope<TPayload> envelope,
        CancellationToken cancellationToken = default)
        where TPayload : class;
}
