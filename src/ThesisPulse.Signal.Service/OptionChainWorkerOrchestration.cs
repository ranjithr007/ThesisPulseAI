namespace ThesisPulse.Signal.Service;

public enum OptionChainWorkExecutionOutcome
{
    Completed,
    Duplicate,
    Rejected,
    RetryableFailure,
    TerminalFailure,
}

public sealed record OptionChainWorkExecutionResult(
    OptionChainWorkExecutionOutcome Outcome,
    string? Reason = null);

public interface IOptionChainWorkHandler
{
    Task<OptionChainWorkExecutionResult> HandleAsync(
        OptionChainWorkItem workItem,
        CancellationToken cancellationToken = default);
}

public sealed record OptionChainWorkerPolicy
{
    public int MaximumAttempts { get; init; } = 5;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (MaximumAttempts is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(MaximumAttempts));
        if (LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
        if (InitialRetryDelay <= TimeSpan.Zero || MaximumRetryDelay < InitialRetryDelay)
            throw new ArgumentOutOfRangeException(nameof(InitialRetryDelay));
    }

    public TimeSpan RetryDelayForAttempt(int attempt)
    {
        var exponent = Math.Max(0, attempt - 1);
        var factor = Math.Pow(2, exponent);
        var candidate = TimeSpan.FromTicks((long)Math.Min(
            InitialRetryDelay.Ticks * factor,
            MaximumRetryDelay.Ticks));
        return candidate;
    }
}

public sealed record OptionChainWorkerRunResult(
    bool WorkFound,
    Guid? WorkUid,
    OptionChainWorkExecutionOutcome? Outcome,
    int AttemptCount,
    DateTimeOffset? RetryAtUtc,
    string? Reason);

public sealed class OptionChainWorkerOrchestrator(
    IOptionChainWorkQueue queue,
    IOptionChainWorkHandler handler,
    OptionChainWorkerPolicy policy,
    TimeProvider timeProvider,
    ILogger<OptionChainWorkerOrchestrator> logger)
{
    public async Task<OptionChainWorkerRunResult> RunOnceAsync(
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        policy.Validate();
        var now = timeProvider.GetUtcNow();
        var lease = await queue.TryLeaseAsync(leaseOwner, now, policy.LeaseDuration, cancellationToken);
        if (lease is null)
            return new(false, null, null, 0, null, null);

        OptionChainWorkExecutionResult execution;
        try
        {
            execution = await handler.HandleAsync(lease.WorkItem, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Option-chain worker failed for work {WorkUid}", lease.WorkItem.WorkUid);
            execution = new(OptionChainWorkExecutionOutcome.RetryableFailure, exception.GetType().Name);
        }

        var completedAt = timeProvider.GetUtcNow();
        if (execution.Outcome == OptionChainWorkExecutionOutcome.RetryableFailure &&
            lease.WorkItem.AttemptCount < policy.MaximumAttempts)
        {
            var retryAt = completedAt.Add(policy.RetryDelayForAttempt(lease.WorkItem.AttemptCount));
            var retried = await queue.RetryAsync(
                lease.WorkItem.WorkUid,
                leaseOwner,
                retryAt,
                execution.Reason ?? "RETRYABLE_FAILURE",
                cancellationToken);
            if (!retried)
                throw new InvalidOperationException("OPTION_CHAIN_WORK_LEASE_LOST");
            return new(true, lease.WorkItem.WorkUid, execution.Outcome, lease.WorkItem.AttemptCount, retryAt, execution.Reason);
        }

        var terminalStatus = execution.Outcome switch
        {
            OptionChainWorkExecutionOutcome.Completed => OptionChainWorkStatus.Completed,
            OptionChainWorkExecutionOutcome.Duplicate => OptionChainWorkStatus.Duplicate,
            OptionChainWorkExecutionOutcome.Rejected => OptionChainWorkStatus.Rejected,
            OptionChainWorkExecutionOutcome.RetryableFailure => OptionChainWorkStatus.Failed,
            OptionChainWorkExecutionOutcome.TerminalFailure => OptionChainWorkStatus.Failed,
            _ => throw new InvalidOperationException("Unsupported option-chain work outcome."),
        };
        var reason = execution.Outcome == OptionChainWorkExecutionOutcome.RetryableFailure
            ? execution.Reason ?? "MAXIMUM_ATTEMPTS_EXCEEDED"
            : execution.Reason;
        var completed = await queue.CompleteAsync(
            lease.WorkItem.WorkUid,
            leaseOwner,
            terminalStatus,
            reason,
            completedAt,
            cancellationToken);
        if (!completed)
            throw new InvalidOperationException("OPTION_CHAIN_WORK_LEASE_LOST");

        return new(true, lease.WorkItem.WorkUid, execution.Outcome, lease.WorkItem.AttemptCount, null, reason);
    }
}
