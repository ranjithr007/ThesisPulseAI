using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed record MarketDataSubscriptionItem(
    string ProviderInstrumentKey,
    string FeedMode,
    string RecoveryTimeframe,
    int Priority);

public sealed record MarketDataSubscriptionPlan(
    string ProviderCode,
    string FeedMode,
    IReadOnlyCollection<MarketDataSubscriptionItem> Items,
    string Version,
    DateTimeOffset GeneratedAtUtc);

public sealed record MarketDataGap(
    string ProviderInstrumentKey,
    string Timeframe,
    DateTimeOffset GapStartUtc,
    DateTimeOffset GapEndUtc,
    int ExpectedRecordCount,
    string ReasonCode);

public sealed record MarketDataRecoveryRunResult(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int SubscriptionCount,
    int GapsDetected,
    int RecoveryRequests,
    int CandlesAccepted,
    int CandlesDuplicated,
    int CandlesRejected,
    IReadOnlyCollection<string> Warnings);

public interface IMarketDataSubscriptionCatalog
{
    Task<MarketDataSubscriptionPlan> GetPlanAsync(
        string providerCode,
        string feedMode,
        CancellationToken cancellationToken = default);
}

public interface IMarketDataGapDetector
{
    MarketDataGap? Detect(
        MarketDataSubscriptionItem subscription,
        IReadOnlyCollection<StoredCandleV1> latestCandles,
        DateTimeOffset evaluatedAtUtc);
}

public interface IMarketDataRecoveryStateStore
{
    Task RecordDetectedAsync(
        MarketDataGap gap,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task RecordRecoveredAsync(
        MarketDataGap gap,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task RecordFailureAsync(
        MarketDataGap gap,
        string correlationId,
        string error,
        CancellationToken cancellationToken = default);
}
