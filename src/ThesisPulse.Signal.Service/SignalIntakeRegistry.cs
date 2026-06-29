using System.Collections.Concurrent;
using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Signal.Service;

public sealed class SignalIntakeRegistry
{
    private readonly ConcurrentDictionary<Guid, SignalGeneratedV1> _signals = new();

    public Task AddAsync(
        SignalGeneratedV1 signal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(signal);

        if (!_signals.TryAdd(signal.SignalUid, signal))
        {
            throw new InvalidOperationException(
                $"Signal '{signal.SignalUid}' is already registered.");
        }

        return Task.CompletedTask;
    }

    public IReadOnlyCollection<SignalGeneratedV1> GetLatest(int maximumCount)
    {
        if (maximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                "Maximum count must be between 1 and 500.");
        }

        return _signals.Values
            .OrderByDescending(signal => signal.GeneratedAtUtc)
            .Take(maximumCount)
            .ToArray();
    }
}
