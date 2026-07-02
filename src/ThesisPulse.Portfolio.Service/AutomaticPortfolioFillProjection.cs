using ThesisPulse.Shared.Contracts.Common.V1;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Infrastructure.Portfolio;

namespace ThesisPulse.Portfolio.Service;

public static class AutomaticPortfolioFillProjectionStatus
{
    public const string Pending = "PENDING";
    public const string Leased = "LEASED";
    public const string Projected = "PROJECTED";
    public const string Duplicate = "DUPLICATE";
    public const string RetryPending = "RETRY_PENDING";
    public const string Rejected = "REJECTED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}

public sealed class AutomaticPortfolioFillProjectionOptions
{
    public const string SectionName = "AutomaticPortfolioFillProjection";

    public bool Enabled { get; init; }
    public int PollIntervalSeconds { get; init; } = 5;
    public int BatchSize { get; init; } = 50;
    public int MaximumAttempts { get; init; } = 5;
    public string ProjectionPolicyVersion { get; init; } =
        "automatic-paper-fill-portfolio-projection-v1.0.0";

    public void Validate()
    {
        if (PollIntervalSeconds is < 1 or > 300)
            throw new InvalidOperationException(
                "AutomaticPortfolioFillProjection:PollIntervalSeconds must be between 1 and 300.");
        if (BatchSize is < 1 or > 500)
            throw new InvalidOperationException(
                "AutomaticPortfolioFillProjection:BatchSize must be between 1 and 500.");
        if (MaximumAttempts is < 1 or > 20)
            throw new InvalidOperationException(
                "AutomaticPortfolioFillProjection:MaximumAttempts must be between 1 and 20.");
        ArgumentException.ThrowIfNullOrWhiteSpace(ProjectionPolicyVersion);
    }
}

public sealed record AutomaticPortfolioFillProjectionCandidate(
    long FillId,
    Guid FillUid,
    long? PortfolioId,
    string? PortfolioCode,
    string CorrelationId,
    DateTimeOffset FillAtUtc,
    IReadOnlyCollection<string> RoutingReasons);

public sealed record AutomaticPortfolioFillProjectionWorkItem(
    long WorkItemId,
    long FillId,
    Guid FillUid,
    long PortfolioId,
    string PortfolioCode,
    Guid RequestUid,
    string CorrelationId,
    DateTimeOffset FillAtUtc,
    int AttemptCount);

public sealed record AutomaticPortfolioFillProjectionEnqueueResult(
    string Outcome,
    Guid FillUid,
    IReadOnlyCollection<string> Reasons);

public interface IAutomaticPortfolioFillProjectionCandidateStore
{
    Task<IReadOnlyCollection<AutomaticPortfolioFillProjectionCandidate>> ReadPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken);
}

public interface IAutomaticPortfolioFillProjectionWorkQueue
{
    Task<AutomaticPortfolioFillProjectionEnqueueResult> EnqueueAsync(
        AutomaticPortfolioFillProjectionCandidate candidate,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<AutomaticPortfolioFillProjectionWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        long workItemId,
        string projectionStatus,
        Guid? positionUid,
        CancellationToken cancellationToken);

    Task RetryAsync(
        long workItemId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken);

    Task RejectAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken);

    Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken);
}

public static class AutomaticPortfolioFillProjectionIdentity
{
    public static Guid RequestUid(Guid fillUid, string policyVersion) =>
        DeterministicGuidV1.Create(
            fillUid,
            $"automatic-portfolio-fill-projection:{policyVersion}");
}

public static class AutomaticPortfolioFillProjectionCandidateValidator
{
    public static IReadOnlyCollection<string> Validate(
        AutomaticPortfolioFillProjectionCandidate candidate)
    {
        var reasons = new List<string>();
        if (candidate.FillId <= 0 || candidate.FillUid == Guid.Empty)
            reasons.Add("AUTHORITATIVE_FILL_IDENTITY_REQUIRED");
        if (!Guid.TryParse(candidate.CorrelationId, out _))
            reasons.Add("CORRELATION_ID_INVALID");
        if (candidate.FillAtUtc == default)
            reasons.Add("FILL_TIMESTAMP_REQUIRED");
        if (candidate.PortfolioId is null || candidate.PortfolioId <= 0 ||
            string.IsNullOrWhiteSpace(candidate.PortfolioCode))
            reasons.Add("PAPER_PORTFOLIO_ROUTING_REQUIRED");
        reasons.AddRange(candidate.RoutingReasons);
        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }
}

public sealed record AutomaticPortfolioFillProjectionWorkerSnapshot(
    long Discovered,
    long Enqueued,
    long RoutingRejected,
    long Leased,
    long Projected,
    long Duplicates,
    long Recovered,
    long Retried,
    long Rejected,
    long Failed);

public sealed class AutomaticPortfolioFillProjectionWorkerState
{
    private long _discovered;
    private long _enqueued;
    private long _routingRejected;
    private long _leased;
    private long _projected;
    private long _duplicates;
    private long _recovered;
    private long _retried;
    private long _rejected;
    private long _failed;

    public void Discovered(int count) => Interlocked.Add(ref _discovered, count);
    public void Enqueued() => Interlocked.Increment(ref _enqueued);
    public void RoutingRejected() => Interlocked.Increment(ref _routingRejected);
    public void Leased(int count) => Interlocked.Add(ref _leased, count);
    public void Projected() => Interlocked.Increment(ref _projected);
    public void Duplicate(bool recovered)
    {
        Interlocked.Increment(ref _duplicates);
        if (recovered)
            Interlocked.Increment(ref _recovered);
    }
    public void Retried() => Interlocked.Increment(ref _retried);
    public void Rejected() => Interlocked.Increment(ref _rejected);
    public void Failed() => Interlocked.Increment(ref _failed);

    public AutomaticPortfolioFillProjectionWorkerSnapshot Snapshot() => new(
        Interlocked.Read(ref _discovered),
        Interlocked.Read(ref _enqueued),
        Interlocked.Read(ref _routingRejected),
        Interlocked.Read(ref _leased),
        Interlocked.Read(ref _projected),
        Interlocked.Read(ref _duplicates),
        Interlocked.Read(ref _recovered),
        Interlocked.Read(ref _retried),
        Interlocked.Read(ref _rejected),
        Interlocked.Read(ref _failed));
}

public sealed class AutomaticPortfolioFillProjectionProcessor(
    AutomaticPortfolioFillProjectionOptions options,
    IAutomaticPortfolioFillProjectionWorkQueue queue,
    IPortfolioLedgerStore portfolioStore,
    AutomaticPortfolioFillProjectionWorkerState state,
    ILogger<AutomaticPortfolioFillProjectionProcessor> logger)
{
    public async Task ProcessAsync(
        AutomaticPortfolioFillProjectionWorkItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await portfolioStore.ProjectFillAsync(
                new PortfolioFillProjectionRequestV1(
                    item.RequestUid,
                    item.FillUid,
                    item.PortfolioCode,
                    item.CorrelationId,
                    item.FillAtUtc),
                cancellationToken);

            if (string.Equals(
                    result.Status,
                    PortfolioLedgerContractV1.Projected,
                    StringComparison.Ordinal))
            {
                await queue.CompleteAsync(
                    item.WorkItemId,
                    AutomaticPortfolioFillProjectionStatus.Projected,
                    result.Position?.PositionUid,
                    cancellationToken);
                state.Projected();
                return;
            }

            if (string.Equals(
                    result.Status,
                    PortfolioLedgerContractV1.Duplicate,
                    StringComparison.Ordinal))
            {
                await queue.CompleteAsync(
                    item.WorkItemId,
                    AutomaticPortfolioFillProjectionStatus.Duplicate,
                    result.Position?.PositionUid,
                    cancellationToken);
                state.Duplicate(item.AttemptCount > 1);
                return;
            }

            await queue.RejectAsync(
                item.WorkItemId,
                result.Reasons.Count > 0
                    ? result.Reasons
                    : new[] { "PORTFOLIO_FILL_PROJECTION_REJECTED" },
                cancellationToken);
            state.Rejected();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (item.AttemptCount >= options.MaximumAttempts)
            {
                await queue.FailAsync(
                    item.WorkItemId,
                    exception.Message,
                    cancellationToken);
                state.Failed();
            }
            else
            {
                await queue.RetryAsync(
                    item.WorkItemId,
                    exception.Message,
                    DateTimeOffset.UtcNow.AddSeconds(BackoffSeconds(item.AttemptCount)),
                    cancellationToken);
                state.Retried();
            }

            logger.LogWarning(
                exception,
                "Automatic portfolio projection work item {WorkItemId} failed at attempt {AttemptCount}.",
                item.WorkItemId,
                item.AttemptCount);
        }
    }

    private static int BackoffSeconds(int attemptCount) =>
        Math.Min(300, 5 * (1 << Math.Min(Math.Max(attemptCount - 1, 0), 6)));
}

public sealed class AutomaticPortfolioFillProjectionIntakeWorker(
    AutomaticPortfolioFillProjectionOptions options,
    IAutomaticPortfolioFillProjectionCandidateStore candidateStore,
    IAutomaticPortfolioFillProjectionWorkQueue queue,
    AutomaticPortfolioFillProjectionWorkerState state,
    ILogger<AutomaticPortfolioFillProjectionIntakeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;

        var delay = TimeSpan.FromSeconds(options.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var candidates = await candidateStore.ReadPendingAsync(
                    options.BatchSize,
                    stoppingToken);
                state.Discovered(candidates.Count);
                foreach (var candidate in candidates)
                {
                    var result = await queue.EnqueueAsync(candidate, stoppingToken);
                    if (string.Equals(result.Outcome, "ENQUEUED", StringComparison.Ordinal))
                        state.Enqueued();
                    else if (string.Equals(
                                 result.Outcome,
                                 AutomaticPortfolioFillProjectionStatus.Rejected,
                                 StringComparison.Ordinal))
                        state.RoutingRejected();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Automatic portfolio fill discovery failed closed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}

public sealed class AutomaticPortfolioFillProjectionWorker(
    AutomaticPortfolioFillProjectionOptions options,
    IAutomaticPortfolioFillProjectionWorkQueue queue,
    AutomaticPortfolioFillProjectionProcessor processor,
    AutomaticPortfolioFillProjectionWorkerState state,
    ILogger<AutomaticPortfolioFillProjectionWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:portfolio-fill";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;

        var delay = TimeSpan.FromSeconds(options.PollIntervalSeconds);
        var leaseDuration = TimeSpan.FromSeconds(Math.Max(30, options.PollIntervalSeconds * 3));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var items = await queue.LeaseAsync(
                    options.BatchSize,
                    _leaseOwner,
                    leaseDuration,
                    stoppingToken);
                state.Leased(items.Count);
                foreach (var item in items)
                    await processor.ProcessAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                state.Failed();
                logger.LogError(exception, "Automatic portfolio fill worker polling failed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}
