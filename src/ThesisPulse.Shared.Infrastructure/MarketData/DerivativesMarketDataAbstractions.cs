using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public interface IDerivativesMarketDataStore
{
    Task<DerivativeContractSynchronizationResultV1> SynchronizeContractsAsync(
        IReadOnlyCollection<CanonicalInstrumentV1> instruments,
        DateTimeOffset snapshotReceivedAtUtc,
        CancellationToken cancellationToken = default);

    Task<FuturesBasisIngestionResultV1> PersistFuturesBasisAsync(
        CanonicalFuturesBasisObservationV1 observation,
        CancellationToken cancellationToken = default);

    Task<OptionChainIngestionResultV1> PersistOptionChainAsync(
        CanonicalOptionChainSnapshotV1 snapshot,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<DerivativeContractReferenceV1>> GetContractsAsync(
        string underlyingProviderInstrumentKey,
        DateOnly? expiryDate,
        string? contractClass,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<DerivativeExpiryReferenceV1>> GetExpiriesAsync(
        string underlyingProviderInstrumentKey,
        string? marketSegment,
        CancellationToken cancellationToken = default);

    Task<StoredFuturesBasisObservationV1?> GetLatestFuturesBasisAsync(
        string futureProviderInstrumentKey,
        DateTimeOffset? asOfUtc,
        CancellationToken cancellationToken = default);

    Task<StoredOptionChainSnapshotV1?> GetLatestOptionChainAsync(
        string underlyingProviderInstrumentKey,
        DateOnly expiryDate,
        DateTimeOffset? asOfUtc,
        CancellationToken cancellationToken = default);
}
