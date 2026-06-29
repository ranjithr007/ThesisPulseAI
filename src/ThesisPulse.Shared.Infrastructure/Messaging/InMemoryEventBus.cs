using System.Collections.Concurrent;
using ThesisPulse.Shared.Contracts.Messaging.V1;

namespace ThesisPulse.Shared.Infrastructure.Messaging;

public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<
        Type,
        ConcurrentDictionary<Guid, Func<object, CancellationToken, Task>>> _handlers = new();

    public IDisposable Subscribe<TPayload>(
        Func<EventEnvelope<TPayload>, CancellationToken, Task> handler)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscriptionId = Guid.NewGuid();
        var handlersForType = _handlers.GetOrAdd(
            typeof(TPayload),
            _ => new ConcurrentDictionary<Guid, Func<object, CancellationToken, Task>>());

        handlersForType[subscriptionId] = (message, cancellationToken) =>
            handler((EventEnvelope<TPayload>)message, cancellationToken);

        return new Subscription(() =>
        {
            handlersForType.TryRemove(subscriptionId, out _);

            if (handlersForType.IsEmpty)
            {
                _handlers.TryRemove(typeof(TPayload), out _);
            }
        });
    }

    public async Task PublishAsync<TPayload>(
        EventEnvelope<TPayload> envelope,
        CancellationToken cancellationToken = default)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_handlers.TryGetValue(typeof(TPayload), out var handlersForType))
        {
            return;
        }

        var handlers = handlersForType.Values.ToArray();
        await Task.WhenAll(
            handlers.Select(handler => handler(envelope, cancellationToken)));
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose()
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }
}
