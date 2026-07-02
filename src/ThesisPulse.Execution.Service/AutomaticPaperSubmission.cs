using System.Net.Http.Json;
using ThesisPulse.Shared.Contracts.Common.V1;
using ThesisPulse.Shared.Contracts.Execution.V1;

namespace ThesisPulse.Execution.Service;

public static class AutomaticPaperSubmissionStatus
{
    public const string Pending = "PENDING";
    public const string Leased = "LEASED";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string Rejected = "REJECTED";
    public const string RetryPending = "RETRY_PENDING";
    public const string Expired = "EXPIRED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}

public sealed class AutomaticPaperSubmissionOptions
{
    public const string SectionName = "AutomaticPaperSubmission";

    public bool Enabled { get; init; }
    public int PollIntervalSeconds { get; init; } = 5;
    public int BatchSize { get; init; } = 50;
    public int MaximumAttempts { get; init; } = 5;
    public int MaximumOperationalSnapshotAgeSeconds { get; init; } = 30;
    public string OperationsServiceBaseUrl { get; init; } = "http://localhost:59485";
    public string InternalApiKey { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 10;

    public void Validate()
    {
        if (PollIntervalSeconds is < 1 or > 300)
            throw new InvalidOperationException("AutomaticPaperSubmission:PollIntervalSeconds must be between 1 and 300.");
        if (BatchSize is < 1 or > 500)
            throw new InvalidOperationException("AutomaticPaperSubmission:BatchSize must be between 1 and 500.");
        if (MaximumAttempts is < 1 or > 20)
            throw new InvalidOperationException("AutomaticPaperSubmission:MaximumAttempts must be between 1 and 20.");
        if (MaximumOperationalSnapshotAgeSeconds is < 1 or > 300)
            throw new InvalidOperationException("AutomaticPaperSubmission:MaximumOperationalSnapshotAgeSeconds must be between 1 and 300.");
        if (TimeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException("AutomaticPaperSubmission:TimeoutSeconds must be between 1 and 120.");
        if (!Enabled)
            return;
        if (!Uri.TryCreate(OperationsServiceBaseUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("AutomaticPaperSubmission:OperationsServiceBaseUrl must be absolute.");
        ArgumentException.ThrowIfNullOrWhiteSpace(InternalApiKey);
    }
}

public sealed record AutomaticPaperSubmissionCandidate(
    long OrderId,
    Guid OrderUid,
    long ExecutionCommandId,
    Guid ExecutionCommandUid,
    string CorrelationId,
    DateTimeOffset ValidUntilUtc);

public sealed record AutomaticPaperSubmissionWorkItem(
    long WorkItemId,
    long OrderId,
    Guid OrderUid,
    long ExecutionCommandId,
    Guid ExecutionCommandUid,
    string CorrelationId,
    DateTimeOffset ValidUntilUtc,
    Guid SubmitEventUid,
    Guid AcknowledgeEventUid,
    Guid ExpireEventUid,
    string BrokerOrderId,
    int AttemptCount);

public sealed record AutomaticPaperSubmissionEnqueueResult(
    string Outcome,
    Guid OrderUid,
    IReadOnlyCollection<string> Reasons);

public interface IAutomaticPaperSubmissionCandidateStore
{
    Task<IReadOnlyCollection<AutomaticPaperSubmissionCandidate>> ReadPendingAsync(
        int maximumCount,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken);
}

public interface IAutomaticPaperSubmissionWorkQueue
{
    Task<AutomaticPaperSubmissionEnqueueResult> EnqueueAsync(
        AutomaticPaperSubmissionCandidate candidate,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<AutomaticPaperSubmissionWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task AcknowledgeAsync(
        long workItemId,
        string brokerOrderId,
        CancellationToken cancellationToken);

    Task RetryAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken);

    Task RejectAsync(
        long workItemId,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken);

    Task ExpireAsync(
        long workItemId,
        string reason,
        CancellationToken cancellationToken);

    Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken);
}

public interface IAutomaticPaperSubmissionContextProvider
{
    Task<ExecutionOperationalStateV1> GetAsync(CancellationToken cancellationToken);
}

public sealed class HttpAutomaticPaperSubmissionContextProvider(
    HttpClient client,
    AutomaticPaperSubmissionOptions options) : IAutomaticPaperSubmissionContextProvider
{
    public async Task<ExecutionOperationalStateV1> GetAsync(
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/internal/v1/execution/operations");
        request.Headers.TryAddWithoutValidation(
            "X-ThesisPulse-Internal-Key",
            options.InternalApiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ExecutionOperationalStateV1>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Operations Service returned an empty execution operational snapshot.");
    }
}

public sealed record AutomaticPaperSubmissionWorkerSnapshot(
    long Leased,
    long Acknowledged,
    long Recovered,
    long Rejected,
    long Expired,
    long Retried,
    long Failed);

public sealed class AutomaticPaperSubmissionWorkerState
{
    private long _leased;
    private long _acknowledged;
    private long _recovered;
    private long _rejected;
    private long _expired;
    private long _retried;
    private long _failed;

    public void Leased(int count) => Interlocked.Add(ref _leased, count);

    public void Acknowledged(bool recovered)
    {
        Interlocked.Increment(ref _acknowledged);
        if (recovered)
            Interlocked.Increment(ref _recovered);
    }

    public void Rejected() => Interlocked.Increment(ref _rejected);
    public void Expired() => Interlocked.Increment(ref _expired);
    public void Retried() => Interlocked.Increment(ref _retried);
    public void Failed() => Interlocked.Increment(ref _failed);

    public AutomaticPaperSubmissionWorkerSnapshot Snapshot() => new(
        Interlocked.Read(ref _leased),
        Interlocked.Read(ref _acknowledged),
        Interlocked.Read(ref _recovered),
        Interlocked.Read(ref _rejected),
        Interlocked.Read(ref _expired),
        Interlocked.Read(ref _retried),
        Interlocked.Read(ref _failed));
}

public sealed class AutomaticPaperSubmissionProcessor(
    AutomaticPaperSubmissionOptions options,
    IAutomaticPaperSubmissionWorkQueue queue,
    IAutomaticPaperSubmissionContextProvider contextProvider,
    IPaperExecutionService executionService,
    AutomaticPaperSubmissionWorkerState state,
    ILogger<AutomaticPaperSubmissionProcessor> logger)
{
    public async Task ProcessAsync(
        AutomaticPaperSubmissionWorkItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = executionService.GetOrder(item.OrderUid);
            if (order is null)
            {
                await queue.RejectAsync(
                    item.WorkItemId,
                    new[] { "PAPER_ORDER_NOT_FOUND" },
                    cancellationToken);
                state.Rejected();
                return;
            }

            if (!string.Equals(
                    order.Environment,
                    ExecutionCommandContractV1.PaperEnvironment,
                    StringComparison.OrdinalIgnoreCase))
            {
                await queue.RejectAsync(
                    item.WorkItemId,
                    new[] { "PAPER_ENVIRONMENT_REQUIRED" },
                    cancellationToken);
                state.Rejected();
                return;
            }

            if (order.State == PaperOrderStateContractV1.Acknowledged)
            {
                await queue.AcknowledgeAsync(
                    item.WorkItemId,
                    order.BrokerOrderId ?? item.BrokerOrderId,
                    cancellationToken);
                state.Acknowledged(item.AttemptCount > 1);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= item.ValidUntilUtc)
            {
                await ExpireAsync(item, order, now, cancellationToken);
                return;
            }

            if (order.State is not PaperOrderStateContractV1.Created and
                not PaperOrderStateContractV1.Submitted)
            {
                await queue.RejectAsync(
                    item.WorkItemId,
                    new[] { "INVALID_AUTOMATIC_SUBMISSION_SOURCE_STATE" },
                    cancellationToken);
                state.Rejected();
                return;
            }

            if (order.State == PaperOrderStateContractV1.Created)
            {
                var operations = await contextProvider.GetAsync(cancellationToken);
                var blockReasons = ValidateOperations(operations, DateTimeOffset.UtcNow);
                if (blockReasons.Count > 0)
                {
                    await RetryOrFailAsync(
                        item,
                        blockReasons,
                        cancellationToken);
                    return;
                }

                var submit = executionService.ApplyEvent(
                    item.OrderUid,
                    new PaperOrderEventRequestV1(
                        item.SubmitEventUid,
                        PaperOrderEventContractV1.Submit,
                        null,
                        null,
                        "AUTOMATIC_INTERNAL_PAPER_SUBMISSION",
                        DateTimeOffset.UtcNow,
                        item.BrokerOrderId));
                if (!submit.Applied || submit.PaperOrder is null)
                {
                    await queue.RejectAsync(
                        item.WorkItemId,
                        submit.Reasons.Count > 0
                            ? submit.Reasons
                            : new[] { "PAPER_SUBMIT_TRANSITION_REJECTED" },
                        cancellationToken);
                    state.Rejected();
                    return;
                }
                order = submit.PaperOrder;
            }

            var acknowledge = executionService.ApplyEvent(
                item.OrderUid,
                new PaperOrderEventRequestV1(
                    item.AcknowledgeEventUid,
                    PaperOrderEventContractV1.Acknowledge,
                    null,
                    null,
                    "AUTOMATIC_INTERNAL_PAPER_ACKNOWLEDGEMENT",
                    DateTimeOffset.UtcNow,
                    item.BrokerOrderId));
            if (!acknowledge.Applied || acknowledge.PaperOrder is null)
            {
                await queue.RejectAsync(
                    item.WorkItemId,
                    acknowledge.Reasons.Count > 0
                        ? acknowledge.Reasons
                        : new[] { "PAPER_ACKNOWLEDGE_TRANSITION_REJECTED" },
                    cancellationToken);
                state.Rejected();
                return;
            }

            var finalOrder = acknowledge.PaperOrder;
            if (finalOrder.State != PaperOrderStateContractV1.Acknowledged ||
                finalOrder.FilledQuantity != 0m ||
                finalOrder.RemainingQuantity != finalOrder.RequestedQuantity ||
                !string.Equals(
                    finalOrder.BrokerOrderId,
                    item.BrokerOrderId,
                    StringComparison.Ordinal))
            {
                await queue.RejectAsync(
                    item.WorkItemId,
                    new[] { "AUTHORITATIVE_ACKNOWLEDGED_ORDER_INVALID" },
                    cancellationToken);
                state.Rejected();
                return;
            }

            await queue.AcknowledgeAsync(
                item.WorkItemId,
                item.BrokerOrderId,
                cancellationToken);
            state.Acknowledged(item.AttemptCount > 1);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (item.AttemptCount >= options.MaximumAttempts)
            {
                await queue.FailAsync(item.WorkItemId, exception.Message, cancellationToken);
                state.Failed();
            }
            else
            {
                await queue.RetryAsync(
                    item.WorkItemId,
                    new[] { "PAPER_SUBMISSION_TRANSIENT_FAILURE" },
                    DateTimeOffset.UtcNow.AddSeconds(BackoffSeconds(item.AttemptCount)),
                    cancellationToken);
                state.Retried();
            }

            logger.LogWarning(
                exception,
                "Automatic PAPER submission work item {WorkItemId} failed on attempt {AttemptCount}.",
                item.WorkItemId,
                item.AttemptCount);
        }
    }

    private async Task ExpireAsync(
        AutomaticPaperSubmissionWorkItem item,
        PaperOrderSnapshotV1 order,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (order.State is PaperOrderStateContractV1.Created or
            PaperOrderStateContractV1.Submitted)
        {
            var transition = executionService.ApplyEvent(
                item.OrderUid,
                new PaperOrderEventRequestV1(
                    item.ExpireEventUid,
                    PaperOrderEventContractV1.Expire,
                    null,
                    null,
                    "EXECUTION_COMMAND_EXPIRED_BEFORE_ACKNOWLEDGEMENT",
                    now,
                    order.BrokerOrderId ?? item.BrokerOrderId));
            if (!transition.Applied)
            {
                await queue.RejectAsync(
                    item.WorkItemId,
                    transition.Reasons,
                    cancellationToken);
                state.Rejected();
                return;
            }
        }

        await queue.ExpireAsync(
            item.WorkItemId,
            "EXECUTION_COMMAND_EXPIRED_BEFORE_ACKNOWLEDGEMENT",
            cancellationToken);
        state.Expired();
    }

    private async Task RetryOrFailAsync(
        AutomaticPaperSubmissionWorkItem item,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken)
    {
        if (item.AttemptCount >= options.MaximumAttempts)
        {
            await queue.FailAsync(
                item.WorkItemId,
                string.Join(',', reasons),
                cancellationToken);
            state.Failed();
            return;
        }

        await queue.RetryAsync(
            item.WorkItemId,
            reasons,
            DateTimeOffset.UtcNow.AddSeconds(BackoffSeconds(item.AttemptCount)),
            cancellationToken);
        state.Retried();
    }

    private IReadOnlyCollection<string> ValidateOperations(
        ExecutionOperationalStateV1 operations,
        DateTimeOffset now)
    {
        var reasons = new List<string>();
        if (operations.KillSwitchActive)
            reasons.Add("KILL_SWITCH_ACTIVE");
        if (operations.TradingHalted)
            reasons.Add("TRADING_HALTED");
        if (!operations.MarketOpen)
            reasons.Add("MARKET_CLOSED");
        if (!operations.MarketDataHealthy)
            reasons.Add("MARKET_DATA_UNHEALTHY");
        if (!operations.PaperGatewayHealthy)
            reasons.Add("PAPER_GATEWAY_UNHEALTHY");
        if (operations.ObservedAtUtc > now ||
            now - operations.ObservedAtUtc >
                TimeSpan.FromSeconds(options.MaximumOperationalSnapshotAgeSeconds))
            reasons.Add("OPERATIONAL_SNAPSHOT_STALE");
        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static int BackoffSeconds(int attemptCount) =>
        Math.Min(300, 5 * (1 << Math.Min(Math.Max(attemptCount - 1, 0), 6)));
}

public sealed class AutomaticPaperSubmissionIntakeWorker(
    AutomaticPaperSubmissionOptions options,
    IAutomaticPaperSubmissionCandidateStore candidateStore,
    IAutomaticPaperSubmissionWorkQueue queue,
    ILogger<AutomaticPaperSubmissionIntakeWorker> logger) : BackgroundService
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
                    DateTimeOffset.UtcNow,
                    stoppingToken);
                foreach (var candidate in candidates)
                    await queue.EnqueueAsync(candidate, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Automatic PAPER submission discovery failed closed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}

public sealed class AutomaticPaperSubmissionWorker(
    AutomaticPaperSubmissionOptions options,
    IAutomaticPaperSubmissionWorkQueue queue,
    AutomaticPaperSubmissionProcessor processor,
    AutomaticPaperSubmissionWorkerState state,
    ILogger<AutomaticPaperSubmissionWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:paper-submission";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;

        var delay = TimeSpan.FromSeconds(options.PollIntervalSeconds);
        var leaseDuration = TimeSpan.FromSeconds(
            Math.Max(30, options.PollIntervalSeconds * 3));
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
                logger.LogError(
                    exception,
                    "Automatic PAPER submission worker polling failed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}

public static class AutomaticPaperSubmissionIdentity
{
    public static Guid SubmitEventUid(Guid orderUid) =>
        DeterministicGuidV1.Create(orderUid, "automatic-paper-submit-v1");

    public static Guid AcknowledgeEventUid(Guid orderUid) =>
        DeterministicGuidV1.Create(orderUid, "automatic-paper-acknowledge-v1");

    public static Guid ExpireEventUid(Guid orderUid) =>
        DeterministicGuidV1.Create(orderUid, "automatic-paper-expire-v1");

    public static string BrokerOrderId(Guid orderUid) =>
        $"PAPER-{orderUid:N}";
}
