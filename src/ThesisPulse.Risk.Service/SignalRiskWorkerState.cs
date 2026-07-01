namespace ThesisPulse.Risk.Service;

public sealed class SignalRiskWorkerState
{
    private long _leased;
    private long _completed;
    private long _duplicates;
    private long _expired;
    private long _retried;
    private long _failed;
    private DateTimeOffset? _lastSuccessUtc;
    private DateTimeOffset? _lastFailureUtc;

    public void Leased(int count) => Interlocked.Add(ref _leased, count);
    public void Completed(bool duplicate)
    {
        Interlocked.Increment(ref _completed);
        if (duplicate) Interlocked.Increment(ref _duplicates);
        _lastSuccessUtc = DateTimeOffset.UtcNow;
    }
    public void Expired()
    {
        Interlocked.Increment(ref _expired);
        _lastSuccessUtc = DateTimeOffset.UtcNow;
    }
    public void Retried()
    {
        Interlocked.Increment(ref _retried);
        _lastFailureUtc = DateTimeOffset.UtcNow;
    }
    public void Failed()
    {
        Interlocked.Increment(ref _failed);
        _lastFailureUtc = DateTimeOffset.UtcNow;
    }

    public object Snapshot() => new
    {
        leased = Interlocked.Read(ref _leased),
        completed = Interlocked.Read(ref _completed),
        duplicates = Interlocked.Read(ref _duplicates),
        expired = Interlocked.Read(ref _expired),
        retried = Interlocked.Read(ref _retried),
        failed = Interlocked.Read(ref _failed),
        lastSuccessUtc = _lastSuccessUtc,
        lastFailureUtc = _lastFailureUtc,
    };
}
