using System.Net.Http.Json;
using ThesisPulse.Shared.Contracts.Common.V1;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;
using ThesisPulse.Shared.Infrastructure.Execution;

namespace ThesisPulse.Execution.Service;

public static class AutomaticPaperExecutionStatus
{
    public const string Pending = "PENDING";
    public const string Leased = "LEASED";
    public const string Authorized = "AUTHORIZED";
    public const string Rejected = "REJECTED";
    public const string RetryPending = "RETRY_PENDING";
    public const string Expired = "EXPIRED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}

public sealed class AutomaticPaperExecutionOptions
{
    public const string SectionName = "AutomaticPaperExecution";

    public bool Enabled { get; init; }
    public int PollIntervalSeconds { get; init; } = 5;
    public int BatchSize { get; init; } = 50;
    public int MaximumAttempts { get; init; } = 5;
    public string OperationsServiceBaseUrl { get; init; } = "http://localhost:59485";
    public string InternalApiKey { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 10;

    public void Validate()
    {
        if (PollIntervalSeconds is < 1 or > 300)
            throw new InvalidOperationException("AutomaticPaperExecution:PollIntervalSeconds must be between 1 and 300.");
        if (BatchSize is < 1 or > 500)
            throw new InvalidOperationException("AutomaticPaperExecution:BatchSize must be between 1 and 500.");
        if (MaximumAttempts is < 1 or > 20)
            throw new InvalidOperationException("AutomaticPaperExecution:MaximumAttempts must be between 1 and 20.");
        if (TimeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException("AutomaticPaperExecution:TimeoutSeconds must be between 1 and 120.");
        if (!Enabled)
            return;
        if (!Uri.TryCreate(OperationsServiceBaseUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("AutomaticPaperExecution:OperationsServiceBaseUrl must be absolute.");
        ArgumentException.ThrowIfNullOrWhiteSpace(InternalApiKey);
    }
}

public sealed record AutomaticPaperExecutionCandidate(
    long TradePlanId,
    Guid SourceMessageUid,
    TradePlanV1 TradePlan);

public sealed record AutomaticPaperExecutionWorkItem(
    long WorkItemId,
    long TradePlanId,
    Guid SourceMessageUid,
    Guid RequestUid,
    string CorrelationId,
    string IdempotencyKey,
    TradePlanV1 TradePlan,
    int AttemptCount);

public sealed record AutomaticPaperExecutionEnqueueResult(
    string Outcome,
    Guid TradePlanUid,
    Guid RequestUid,
    IReadOnlyCollection<string> Reasons);

public interface IAutomaticPaperExecutionCandidateStore
{
    Task<IReadOnlyCollection<AutomaticPaperExecutionCandidate>> ReadPendingAsync(
        int maximumCount,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken);
}

public interface IAutomaticPaperExecutionWorkQueue
{
    Task<AutomaticPaperExecutionEnqueueResult> EnqueueAsync(
        AutomaticPaperExecutionCandidate candidate,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<AutomaticPaperExecutionWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task AuthorizeAsync(
        long workItemId,
        Guid executionCommandUid,
        Guid orderUid,
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

    Task ExpireAsync(
        long workItemId,
        string reason,
        CancellationToken cancellationToken);

    Task FailAsync(
        long workItemId,
        string error,
        CancellationToken cancellationToken);
}

public interface IAutomaticPaperExecutionContextProvider
{
    Task<ExecutionOperationalStateV1> GetAsync(CancellationToken cancellationToken);
}

public sealed class HttpAutomaticPaperExecutionContextProvider(
    HttpClient client,
    AutomaticPaperExecutionOptions options) : IAutomaticPaperExecutionContextProvider
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

public sealed record AutomaticPaperExecutionWorkerSnapshot(
    long Leased,
    long Authorized,
    long Recovered,
    long Rejected,
    long Expired,
    long Retried,
    long Failed);

public sealed class AutomaticPaperExecutionWorkerState
{
    private long _leased;
    private long _authorized;
    private long _recovered;
    private long _rejected;
    private long _expired;
    private long _retried;
    private long _failed;

    public void Leased(int count) => Interlocked.Add(ref _leased, count);

    public void Authorized(bool recovered)
    {
        Interlocked.Increment(ref _authorized);
        if (recovered)
            Interlocked.Increment(ref _recovered);
    }

    public void Rejected() => Interlocked.Increment(ref _rejected);
    public void Expired() => Interlocked.Increment(ref _expired);
    public void Retried() => Interlocked.Increment(ref _retried);
    public void Failed() => Interlocked.Increment(ref _failed);

    public AutomaticPaperExecutionWorkerSnapshot Snapshot() => new(
        Interlocked.Read(ref _leased),
        Interlocked.Read(ref _authorized),
        Interlocked.Read(ref _recovered),
        Interlocked.Read(ref _rejected),
        Interlocked.Read(ref _expired),
        Interlocked.Read(ref _retried),
        Interlocked.Read(ref _failed));
}

public sealed class AutomaticPaperExecutionProcessor(
    AutomaticPaperExecutionOptions options,
    IAutomaticPaperExecutionWorkQueue queue,
    IAutomaticPaperExecutionContextProvider contextProvider,
    IPaperExecutionService executionService,
    AutomaticPaperExecutionWorkerState state,
    ILogger<AutomaticPaperExecutionProcessor> logger)
{
    public async Task ProcessAsync(
        AutomaticPaperExecutionWorkItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= item.TradePlan.ValidUntilUtc)
            {
                await queue.ExpireAsync(
                    item.WorkItemId,
                    "TRADE_PLAN_EXPIRED",
                    cancellationToken);
                state.Expired();
                return;
            }

            var validationReasons = Validate(item.TradePlan, item.CorrelationId);
            if (validationReasons.Count > 0)
            {
                await queue.RejectAsync(
                    item.WorkItemId,
                    validationReasons,
                    cancellationToken);
                state.Rejected();
                return;
            }

            var operations = await contextProvider.GetAsync(cancellationToken);
            now = DateTimeOffset.UtcNow;
            var request = new ExecutionCommandRequestV1(
                item.RequestUid,
                item.IdempotencyKey,
                item.CorrelationId,
                item.TradePlan,
                operations,
                item.TradePlan.ExecutionPolicyVersion,
                now);
            var result = executionService.Authorize(request);
            if (result.Status != ExecutionCommandContractV1.Authorized ||
                result.Command is null ||
                result.PaperOrder is null)
            {
                var reasons = result.Reasons.Count > 0
                    ? result.Reasons
                    : new[] { "EXECUTION_AUTHORIZATION_REJECTED" };
                await queue.RejectAsync(item.WorkItemId, reasons, cancellationToken);
                state.Rejected();
                return;
            }

            if (!string.Equals(
                    result.PaperOrder.State,
                    PaperOrderStateContractV1.Created,
                    StringComparison.Ordinal))
            {
                await queue.RejectAsync(
                    item.WorkItemId,
                    new[] { "AUTHORITATIVE_ORDER_NOT_CREATED" },
                    cancellationToken);
                state.Rejected();
                return;
            }

            await queue.AuthorizeAsync(
                item.WorkItemId,
                result.Command.ExecutionCommandUid,
                result.PaperOrder.PaperOrderUid,
                cancellationToken);
            state.Authorized(item.AttemptCount > 1);
        }
        catch (PaperExecutionIdempotencyConflictException exception)
        {
            await queue.RejectAsync(
                item.WorkItemId,
                new[] { "EXECUTION_IDEMPOTENCY_CONFLICT" },
                cancellationToken);
            state.Rejected();
            logger.LogWarning(
                exception,
                "Automatic PAPER execution work item {WorkItemId} has an idempotency conflict.",
                item.WorkItemId);
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
                var delaySeconds = Math.Min(
                    300,
                    5 * (1 << Math.Min(Math.Max(item.AttemptCount - 1, 0), 6)));
                await queue.RetryAsync(
                    item.WorkItemId,
                    exception.Message,
                    DateTimeOffset.UtcNow.AddSeconds(delaySeconds),
                    cancellationToken);
                state.Retried();
            }

            logger.LogWarning(
                exception,
                "Automatic PAPER execution work item {WorkItemId} failed on attempt {AttemptCount}.",
                item.WorkItemId,
                item.AttemptCount);
        }
    }

    private static IReadOnlyCollection<string> Validate(
        TradePlanV1 plan,
        string correlationId)
    {
        var reasons = new List<string>();
        if (plan.TradePlanUid == Guid.Empty ||
            plan.RiskDecisionUid == Guid.Empty ||
            plan.ThesisUid == Guid.Empty ||
            plan.SignalUid == Guid.Empty)
            reasons.Add("TRADE_PLAN_LINEAGE_REQUIRED");
        if (!Guid.TryParse(correlationId, out _) ||
            !string.Equals(plan.CorrelationId, correlationId, StringComparison.Ordinal))
            reasons.Add("CORRELATION_LINEAGE_INVALID");
        if (!string.Equals(plan.Status, TradePlanContractV1.Ready, StringComparison.Ordinal))
            reasons.Add("PLAN_READY_REQUIRED");
        if (!string.Equals(
                plan.Environment,
                ExecutionCommandContractV1.PaperEnvironment,
                StringComparison.OrdinalIgnoreCase))
            reasons.Add("PAPER_ENVIRONMENT_REQUIRED");
        if (plan.ExecutionAuthorized)
            reasons.Add("UPSTREAM_EXECUTION_AUTHORITY_FORBIDDEN");
        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }
}

public sealed class AutomaticPaperExecutionIntakeWorker(
    AutomaticPaperExecutionOptions options,
    IAutomaticPaperExecutionCandidateStore candidateStore,
    IAutomaticPaperExecutionWorkQueue queue,
    ILogger<AutomaticPaperExecutionIntakeWorker> logger) : BackgroundService
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
                    "Automatic PLAN_READY-to-PAPER execution discovery failed closed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}

public sealed class AutomaticPaperExecutionWorker(
    AutomaticPaperExecutionOptions options,
    IAutomaticPaperExecutionWorkQueue queue,
    AutomaticPaperExecutionProcessor processor,
    AutomaticPaperExecutionWorkerState state,
    ILogger<AutomaticPaperExecutionWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:paper-execution";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;

        var pollDelay = TimeSpan.FromSeconds(options.PollIntervalSeconds);
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
                    "Automatic PAPER execution worker polling failed.");
            }

            await Task.Delay(pollDelay, stoppingToken);
        }
    }
}

public static class AutomaticPaperExecutionIdentity
{
    public static Guid RequestUid(Guid tradePlanUid) =>
        DeterministicGuidV1.Create(
            tradePlanUid,
            "automatic-paper-execution-request-v1");

    public static string IdempotencyKey(Guid tradePlanUid) =>
        $"trade-plan:{tradePlanUid:N}:paper-execution:v1";
}
