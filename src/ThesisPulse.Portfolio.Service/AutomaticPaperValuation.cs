using System.Net.Http.Json;
using ThesisPulse.Shared.Contracts.Common.V1;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Contracts.Portfolio.V1;

namespace ThesisPulse.Portfolio.Service;

public static class AutomaticPaperValuationStatus
{
    public const string Pending = "PENDING";
    public const string Leased = "LEASED";
    public const string Valued = "VALUED";
    public const string Duplicate = "DUPLICATE";
    public const string RetryPending = "RETRY_PENDING";
    public const string Rejected = "REJECTED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}

public sealed class AutomaticPaperValuationOptions
{
    public const string SectionName = "AutomaticPaperValuation";

    public bool Enabled { get; init; }
    public int PollIntervalSeconds { get; init; } = 30;
    public int BatchSize { get; init; } = 25;
    public int MaximumAttempts { get; init; } = 5;
    public int MaximumCandlesPerInstrument { get; init; } = 20;
    public int MaximumCandleAgeSeconds { get; init; } = 180;
    public int DeferSeconds { get; init; } = 15;
    public int TimeoutSeconds { get; init; } = 10;
    public string MarketDataServiceBaseUrl { get; init; } = "http://localhost:5101";
    public string MarketDataBrokerCode { get; init; } = "UPSTOX";
    public string ValuationPolicyVersion { get; init; } =
        "deterministic-paper-valuation-v1.0.0";

    public void Validate()
    {
        if (PollIntervalSeconds is < 1 or > 900)
            throw new InvalidOperationException(
                "AutomaticPaperValuation:PollIntervalSeconds must be between 1 and 900.");
        if (BatchSize is < 1 or > 250)
            throw new InvalidOperationException(
                "AutomaticPaperValuation:BatchSize must be between 1 and 250.");
        if (MaximumAttempts is < 1 or > 20)
            throw new InvalidOperationException(
                "AutomaticPaperValuation:MaximumAttempts must be between 1 and 20.");
        if (MaximumCandlesPerInstrument is < 1 or > 500)
            throw new InvalidOperationException(
                "AutomaticPaperValuation:MaximumCandlesPerInstrument must be between 1 and 500.");
        if (MaximumCandleAgeSeconds is < 30 or > 86400)
            throw new InvalidOperationException(
                "AutomaticPaperValuation:MaximumCandleAgeSeconds must be between 30 and 86400.");
        if (DeferSeconds is < 1 or > 900)
            throw new InvalidOperationException(
                "AutomaticPaperValuation:DeferSeconds must be between 1 and 900.");
        if (TimeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException(
                "AutomaticPaperValuation:TimeoutSeconds must be between 1 and 120.");
        ArgumentException.ThrowIfNullOrWhiteSpace(MarketDataBrokerCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(ValuationPolicyVersion);
        if (Enabled && !Uri.TryCreate(MarketDataServiceBaseUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException(
                "AutomaticPaperValuation:MarketDataServiceBaseUrl must be absolute.");
    }
}

public sealed record AutomaticPaperValuationPositionCandidate(
    long PositionId,
    Guid PositionUid,
    long InstrumentId,
    string InstrumentKey,
    string ProductType,
    string Direction,
    decimal Quantity,
    decimal AverageOpenPrice,
    decimal CostBasisAmount,
    decimal RealizedPnlAmount,
    decimal AccruedFeesAmount,
    decimal AccruedTaxesAmount,
    int PositionVersion,
    string? ProviderInstrumentKey);

public sealed record AutomaticPaperValuationPortfolioCandidate(
    long PortfolioId,
    Guid PortfolioUid,
    string PortfolioCode,
    string CurrencyCode,
    DateTimeOffset? LastSnapshotAsOfUtc,
    IReadOnlyCollection<AutomaticPaperValuationPositionCandidate> Positions);

public sealed record AutomaticPaperPositionValuation(
    long PositionId,
    Guid PositionUid,
    long InstrumentId,
    string InstrumentKey,
    string ProductType,
    string Direction,
    decimal Quantity,
    decimal AverageOpenPrice,
    decimal CostBasisAmount,
    decimal RealizedPnlAmount,
    decimal AccruedFeesAmount,
    decimal AccruedTaxesAmount,
    int PositionVersion,
    string ProviderInstrumentKey,
    StoredCandleV1 Candle,
    decimal MarkPrice,
    decimal MarketValueAmount,
    decimal UnrealizedPnlAmount,
    decimal GrossExposureAmount,
    decimal NetExposureAmount,
    decimal NetPnlAmount);

public sealed record AutomaticPaperValuationDecision(
    string Outcome,
    DateTimeOffset? AsOfUtc,
    IReadOnlyCollection<AutomaticPaperPositionValuation> Positions,
    decimal RealizedPnlAmount,
    decimal UnrealizedPnlAmount,
    decimal GrossPnlAmount,
    decimal FeesAmount,
    decimal TaxesAmount,
    decimal NetPnlAmount,
    decimal GrossExposureAmount,
    decimal NetExposureAmount,
    IReadOnlyCollection<string> Reasons);

public sealed record AutomaticPaperValuationPayload(
    Guid RequestUid,
    Guid SnapshotUid,
    string PolicyVersion,
    AutomaticPaperValuationPortfolioCandidate Portfolio,
    AutomaticPaperValuationDecision Decision,
    DateTimeOffset CreatedAtUtc);

public sealed record AutomaticPaperValuationWorkItem(
    long WorkItemId,
    Guid RequestUid,
    Guid SnapshotUid,
    long PortfolioId,
    string PortfolioCode,
    DateTimeOffset AsOfUtc,
    AutomaticPaperValuationPayload Payload,
    int AttemptCount);

public sealed record AutomaticPaperValuationEnqueueResult(
    string Outcome,
    Guid RequestUid,
    IReadOnlyCollection<string> Reasons);

public interface IAutomaticPaperValuationCandidateStore
{
    Task<IReadOnlyCollection<AutomaticPaperValuationPortfolioCandidate>> ReadPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken);
}

public interface IAutomaticPaperValuationMarketDataProvider
{
    Task<IReadOnlyCollection<StoredCandleV1>> GetClosedCandlesAsync(
        string providerInstrumentKey,
        int maximumCount,
        CancellationToken cancellationToken);
}

public interface IAutomaticPaperValuationWorkQueue
{
    Task<AutomaticPaperValuationEnqueueResult> EnqueueAsync(
        AutomaticPaperValuationPayload payload,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<AutomaticPaperValuationWorkItem>> LeaseAsync(
        int maximumCount,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        long workItemId,
        string resultStatus,
        Guid snapshotUid,
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

public interface IPaperPortfolioValuationLedgerStore
{
    Task<PortfolioValuationPersistenceResultV1> PersistAsync(
        AutomaticPaperValuationWorkItem workItem,
        CancellationToken cancellationToken);

    Task<PortfolioPnlSnapshotV1?> GetLatestAsync(
        string portfolioCode,
        CancellationToken cancellationToken);
}

public static class AutomaticPaperValuationIdentity
{
    public static string PositionFingerprint(
        AutomaticPaperValuationPortfolioCandidate candidate) =>
        string.Join(
            ";",
            candidate.Positions
                .OrderBy(position => position.PositionId)
                .Select(position =>
                    $"{position.PositionId}:{position.PositionVersion}:{position.Quantity:0.######}"));

    public static Guid RequestUid(
        AutomaticPaperValuationPortfolioCandidate candidate,
        DateTimeOffset asOfUtc,
        string policyVersion) =>
        DeterministicGuidV1.Create(
            candidate.PortfolioUid,
            $"paper-valuation-request:{asOfUtc:O}:{policyVersion}:{PositionFingerprint(candidate)}");

    public static Guid SnapshotUid(Guid requestUid) =>
        DeterministicGuidV1.Create(requestUid, "paper-pnl-snapshot-v1");

    public static Guid ValuationMarkUid(AutomaticPaperPositionValuation valuation) =>
        DeterministicGuidV1.Create(
            valuation.Candle.CandleUid,
            $"portfolio-valuation-mark:{valuation.InstrumentId}:v1");

    public static Guid PositionValuationUid(
        AutomaticPaperPositionValuation valuation) =>
        DeterministicGuidV1.Create(
            valuation.PositionUid,
            $"position-valuation:{valuation.Candle.CandleUid:D}:{valuation.PositionVersion}:v1");
}

public sealed class DeterministicPaperValuationPolicy(
    AutomaticPaperValuationOptions options)
{
    public AutomaticPaperValuationDecision Evaluate(
        AutomaticPaperValuationPortfolioCandidate candidate,
        IReadOnlyDictionary<long, IReadOnlyCollection<StoredCandleV1>> candlesByPosition,
        DateTimeOffset evaluatedAtUtc)
    {
        var validation = ValidateCandidate(candidate);
        if (validation.Count > 0)
            return Reject(validation);

        var usableByPosition = new Dictionary<long, IReadOnlyDictionary<DateTimeOffset, StoredCandleV1>>();
        foreach (var position in candidate.Positions)
        {
            if (!candlesByPosition.TryGetValue(position.PositionId, out var candles))
            {
                return Defer("POSITION_CANDLES_NOT_AVAILABLE");
            }

            var usable = candles
                .Where(candle => IsUsable(candle, evaluatedAtUtc))
                .GroupBy(candle => candle.CloseAtUtc)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(candle => candle.CandleId).First());
            if (usable.Count == 0)
                return Defer("VALID_FRESH_CANDLE_NOT_AVAILABLE");
            usableByPosition[position.PositionId] = usable;
        }

        HashSet<DateTimeOffset>? common = null;
        foreach (var usable in usableByPosition.Values)
        {
            common = common is null
                ? usable.Keys.ToHashSet()
                : common.Intersect(usable.Keys).ToHashSet();
        }

        if (common is null || common.Count == 0)
            return Defer("COMMON_CLOSED_CANDLE_NOT_AVAILABLE");

        var asOfUtc = common.Max();
        if (candidate.LastSnapshotAsOfUtc.HasValue &&
            asOfUtc <= candidate.LastSnapshotAsOfUtc.Value)
        {
            return new AutomaticPaperValuationDecision(
                PortfolioValuationContractV1.Duplicate,
                asOfUtc,
                Array.Empty<AutomaticPaperPositionValuation>(),
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                Array.Empty<string>());
        }

        var valuations = candidate.Positions
            .OrderBy(position => position.PositionId)
            .Select(position => Calculate(
                position,
                usableByPosition[position.PositionId][asOfUtc]))
            .ToArray();
        var realized = Round(valuations.Sum(value => value.RealizedPnlAmount));
        var unrealized = Round(valuations.Sum(value => value.UnrealizedPnlAmount));
        var fees = Round(valuations.Sum(value => value.AccruedFeesAmount));
        var taxes = Round(valuations.Sum(value => value.AccruedTaxesAmount));
        var netPnl = Round(realized + unrealized);
        var grossPnl = Round(netPnl + fees + taxes);
        var grossExposure = Round(valuations.Sum(value => value.GrossExposureAmount));
        var netExposure = Round(valuations.Sum(value => value.NetExposureAmount));

        return new AutomaticPaperValuationDecision(
            PortfolioValuationContractV1.Valued,
            asOfUtc,
            valuations,
            realized,
            unrealized,
            grossPnl,
            fees,
            taxes,
            netPnl,
            grossExposure,
            netExposure,
            Array.Empty<string>());
    }

    private IReadOnlyCollection<string> ValidateCandidate(
        AutomaticPaperValuationPortfolioCandidate candidate)
    {
        var reasons = new List<string>();
        if (candidate.PortfolioId <= 0 || candidate.PortfolioUid == Guid.Empty ||
            string.IsNullOrWhiteSpace(candidate.PortfolioCode))
            reasons.Add("PORTFOLIO_IDENTITY_REQUIRED");
        if (string.IsNullOrWhiteSpace(candidate.CurrencyCode))
            reasons.Add("PORTFOLIO_CURRENCY_REQUIRED");
        if (candidate.Positions.Count == 0)
            reasons.Add("OPEN_POSITION_REQUIRED");
        foreach (var position in candidate.Positions)
        {
            if (position.PositionId <= 0 || position.PositionUid == Guid.Empty ||
                position.InstrumentId <= 0)
                reasons.Add("POSITION_IDENTITY_REQUIRED");
            if (position.Quantity <= 0 || position.AverageOpenPrice <= 0 ||
                position.CostBasisAmount < 0 || position.PositionVersion < 1)
                reasons.Add("POSITION_STATE_INVALID");
            if (position.Direction is not "LONG" and not "SHORT")
                reasons.Add("POSITION_DIRECTION_INVALID");
            if (string.IsNullOrWhiteSpace(position.ProviderInstrumentKey))
                reasons.Add("MARKET_DATA_INSTRUMENT_MAPPING_REQUIRED");
        }
        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }

    private bool IsUsable(StoredCandleV1 candle, DateTimeOffset evaluatedAtUtc)
    {
        if (!candle.IsClosed || candle.ClosePrice <= 0 ||
            candle.CloseAtUtc > evaluatedAtUtc ||
            !candle.IsUsableForNewExposure ||
            !string.Equals(
                candle.QualityStatus,
                MarketDataQualityStatusV1.Valid,
                StringComparison.OrdinalIgnoreCase))
            return false;

        var age = evaluatedAtUtc - candle.CloseAtUtc;
        return age >= TimeSpan.Zero &&
            age <= TimeSpan.FromSeconds(options.MaximumCandleAgeSeconds);
    }

    private static AutomaticPaperPositionValuation Calculate(
        AutomaticPaperValuationPositionCandidate position,
        StoredCandleV1 candle)
    {
        var mark = Round(candle.ClosePrice);
        var marketValue = Round(position.Quantity * mark);
        var unrealized = position.Direction switch
        {
            "LONG" => Round((mark - position.AverageOpenPrice) * position.Quantity),
            "SHORT" => Round((position.AverageOpenPrice - mark) * position.Quantity),
            _ => throw new InvalidOperationException("Unsupported position direction."),
        };
        var netExposure = position.Direction == "LONG" ? marketValue : -marketValue;
        return new AutomaticPaperPositionValuation(
            position.PositionId,
            position.PositionUid,
            position.InstrumentId,
            position.InstrumentKey,
            position.ProductType,
            position.Direction,
            position.Quantity,
            position.AverageOpenPrice,
            position.CostBasisAmount,
            position.RealizedPnlAmount,
            position.AccruedFeesAmount,
            position.AccruedTaxesAmount,
            position.PositionVersion,
            position.ProviderInstrumentKey!,
            candle,
            mark,
            marketValue,
            unrealized,
            marketValue,
            netExposure,
            Round(position.RealizedPnlAmount + unrealized));
    }

    private static AutomaticPaperValuationDecision Defer(string reason) =>
        new(
            PortfolioValuationContractV1.Deferred,
            null,
            Array.Empty<AutomaticPaperPositionValuation>(),
            0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m,
            new[] { reason });

    private static AutomaticPaperValuationDecision Reject(
        IReadOnlyCollection<string> reasons) =>
        new(
            PortfolioValuationContractV1.Rejected,
            null,
            Array.Empty<AutomaticPaperPositionValuation>(),
            0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m,
            reasons);

    internal static decimal Round(decimal value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero);
}

public sealed record PortfolioMarketDataCandleResponse(
    string InstrumentKey,
    string Timeframe,
    IReadOnlyCollection<StoredCandleV1> Candles,
    int Count);

public sealed class HttpAutomaticPaperValuationMarketDataProvider(HttpClient client)
    : IAutomaticPaperValuationMarketDataProvider
{
    public async Task<IReadOnlyCollection<StoredCandleV1>> GetClosedCandlesAsync(
        string providerInstrumentKey,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        var uri = $"/api/v1/candles?instrumentKey={Uri.EscapeDataString(providerInstrumentKey)}&timeframe=1m&limit={maximumCount}";
        var response = await client.GetFromJsonAsync<PortfolioMarketDataCandleResponse>(
            uri,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Market Data Service returned an empty valuation candle response.");
        return response.Candles
            .OrderByDescending(candle => candle.CloseAtUtc)
            .ThenByDescending(candle => candle.CandleId)
            .ToArray();
    }
}

public sealed record AutomaticPaperValuationWorkerSnapshot(
    long Discovered,
    long Enqueued,
    long Deferred,
    long CandidateRejected,
    long Leased,
    long Valued,
    long Duplicates,
    long Recovered,
    long Retried,
    long Rejected,
    long Failed);

public sealed class AutomaticPaperValuationWorkerState
{
    private long _discovered;
    private long _enqueued;
    private long _deferred;
    private long _candidateRejected;
    private long _leased;
    private long _valued;
    private long _duplicates;
    private long _recovered;
    private long _retried;
    private long _rejected;
    private long _failed;

    public void Discovered(int value) => Interlocked.Add(ref _discovered, value);
    public void Enqueued() => Interlocked.Increment(ref _enqueued);
    public void Deferred() => Interlocked.Increment(ref _deferred);
    public void CandidateRejected() => Interlocked.Increment(ref _candidateRejected);
    public void Leased(int value) => Interlocked.Add(ref _leased, value);
    public void Valued() => Interlocked.Increment(ref _valued);
    public void Duplicate(bool recovered)
    {
        Interlocked.Increment(ref _duplicates);
        if (recovered)
            Interlocked.Increment(ref _recovered);
    }
    public void Retried() => Interlocked.Increment(ref _retried);
    public void Rejected() => Interlocked.Increment(ref _rejected);
    public void Failed() => Interlocked.Increment(ref _failed);

    public AutomaticPaperValuationWorkerSnapshot Snapshot() => new(
        Interlocked.Read(ref _discovered),
        Interlocked.Read(ref _enqueued),
        Interlocked.Read(ref _deferred),
        Interlocked.Read(ref _candidateRejected),
        Interlocked.Read(ref _leased),
        Interlocked.Read(ref _valued),
        Interlocked.Read(ref _duplicates),
        Interlocked.Read(ref _recovered),
        Interlocked.Read(ref _retried),
        Interlocked.Read(ref _rejected),
        Interlocked.Read(ref _failed));
}

public sealed class AutomaticPaperValuationProcessor(
    AutomaticPaperValuationOptions options,
    IAutomaticPaperValuationWorkQueue queue,
    IPaperPortfolioValuationLedgerStore ledgerStore,
    AutomaticPaperValuationWorkerState state,
    ILogger<AutomaticPaperValuationProcessor> logger)
{
    public async Task ProcessAsync(
        AutomaticPaperValuationWorkItem workItem,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ledgerStore.PersistAsync(workItem, cancellationToken);
            if (result.Status == PortfolioValuationContractV1.Valued)
            {
                await queue.CompleteAsync(
                    workItem.WorkItemId,
                    AutomaticPaperValuationStatus.Valued,
                    workItem.SnapshotUid,
                    cancellationToken);
                state.Valued();
                return;
            }

            if (result.Status == PortfolioValuationContractV1.Duplicate)
            {
                await queue.CompleteAsync(
                    workItem.WorkItemId,
                    AutomaticPaperValuationStatus.Duplicate,
                    workItem.SnapshotUid,
                    cancellationToken);
                state.Duplicate(workItem.AttemptCount > 1);
                return;
            }

            await queue.RejectAsync(
                workItem.WorkItemId,
                result.Reasons.Count > 0
                    ? result.Reasons
                    : new[] { "PORTFOLIO_VALUATION_REJECTED" },
                cancellationToken);
            state.Rejected();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (workItem.AttemptCount >= options.MaximumAttempts)
            {
                await queue.FailAsync(
                    workItem.WorkItemId,
                    exception.Message,
                    cancellationToken);
                state.Failed();
            }
            else
            {
                await queue.RetryAsync(
                    workItem.WorkItemId,
                    exception.Message,
                    DateTimeOffset.UtcNow.AddSeconds(BackoffSeconds(workItem.AttemptCount)),
                    cancellationToken);
                state.Retried();
            }

            logger.LogWarning(
                exception,
                "Automatic PAPER valuation work item {WorkItemId} failed at attempt {AttemptCount}.",
                workItem.WorkItemId,
                workItem.AttemptCount);
        }
    }

    private static int BackoffSeconds(int attemptCount) =>
        Math.Min(300, 5 * (1 << Math.Min(Math.Max(attemptCount - 1, 0), 6)));
}

public sealed class AutomaticPaperValuationIntakeWorker(
    AutomaticPaperValuationOptions options,
    IAutomaticPaperValuationCandidateStore candidateStore,
    IAutomaticPaperValuationMarketDataProvider marketDataProvider,
    DeterministicPaperValuationPolicy policy,
    IAutomaticPaperValuationWorkQueue queue,
    AutomaticPaperValuationWorkerState state,
    ILogger<AutomaticPaperValuationIntakeWorker> logger) : BackgroundService
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
                    await ProcessCandidateAsync(candidate, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Automatic PAPER valuation discovery failed closed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task ProcessCandidateAsync(
        AutomaticPaperValuationPortfolioCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            var candles = new Dictionary<long, IReadOnlyCollection<StoredCandleV1>>();
            foreach (var position in candidate.Positions)
            {
                if (string.IsNullOrWhiteSpace(position.ProviderInstrumentKey))
                {
                    candles[position.PositionId] = Array.Empty<StoredCandleV1>();
                    continue;
                }
                candles[position.PositionId] = await marketDataProvider.GetClosedCandlesAsync(
                    position.ProviderInstrumentKey,
                    options.MaximumCandlesPerInstrument,
                    cancellationToken);
            }

            var decision = policy.Evaluate(candidate, candles, DateTimeOffset.UtcNow);
            if (decision.Outcome == PortfolioValuationContractV1.Deferred)
            {
                state.Deferred();
                return;
            }
            if (decision.Outcome == PortfolioValuationContractV1.Duplicate)
            {
                state.Duplicate(false);
                return;
            }
            if (decision.Outcome == PortfolioValuationContractV1.Rejected ||
                decision.AsOfUtc is null)
            {
                state.CandidateRejected();
                return;
            }

            var requestUid = AutomaticPaperValuationIdentity.RequestUid(
                candidate,
                decision.AsOfUtc.Value,
                options.ValuationPolicyVersion);
            var payload = new AutomaticPaperValuationPayload(
                requestUid,
                AutomaticPaperValuationIdentity.SnapshotUid(requestUid),
                options.ValuationPolicyVersion,
                candidate,
                decision,
                DateTimeOffset.UtcNow);
            var result = await queue.EnqueueAsync(payload, cancellationToken);
            if (result.Outcome == "ENQUEUED")
                state.Enqueued();
            else if (result.Outcome == AutomaticPaperValuationStatus.Duplicate)
                state.Duplicate(false);
            else if (result.Outcome == AutomaticPaperValuationStatus.Rejected)
                state.CandidateRejected();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            state.Deferred();
            logger.LogWarning(
                exception,
                "PAPER valuation candidate {PortfolioCode} was deferred.",
                candidate.PortfolioCode);
        }
    }
}

public sealed class AutomaticPaperValuationWorker(
    AutomaticPaperValuationOptions options,
    IAutomaticPaperValuationWorkQueue queue,
    AutomaticPaperValuationProcessor processor,
    AutomaticPaperValuationWorkerState state,
    ILogger<AutomaticPaperValuationWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:paper-valuation";

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
                var workItems = await queue.LeaseAsync(
                    options.BatchSize,
                    _leaseOwner,
                    leaseDuration,
                    stoppingToken);
                state.Leased(workItems.Count);
                foreach (var workItem in workItems)
                    await processor.ProcessAsync(workItem, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                state.Failed();
                logger.LogError(exception, "Automatic PAPER valuation polling failed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}
