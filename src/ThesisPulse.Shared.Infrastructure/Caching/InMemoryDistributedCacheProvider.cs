using System.Collections.Concurrent;
using ThesisPulse.Shared.Infrastructure.Time;

namespace ThesisPulse.Shared.Infrastructure.Caching;

public sealed class InMemoryDistributedCacheProvider(IClock clock)
    : IDistributedCacheProvider
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();

    public Task<T?> GetAsync<T>(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!_entries.TryGetValue(key, out var entry))
        {
            return Task.FromResult(default(T));
        }

        if (entry.ExpiresAtUtc <= clock.UtcNow)
        {
            _entries.TryRemove(key, out _);
            return Task.FromResult(default(T));
        }

        return Task.FromResult(entry.Value is T value ? value : default);
    }

    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        if (expiry <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiry),
                "Cache expiry must be greater than zero.");
        }

        _entries[key] = new CacheEntry(value, clock.UtcNow.Add(expiry));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private sealed record CacheEntry(
        object Value,
        DateTimeOffset ExpiresAtUtc);
}
