using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed class PublishingInMemoryDerivativesMarketDataStore(
    InMemoryDerivativesMarketDataStore inner,
    MarketDataPublicationFactory publicationFactory,
    IMarketDataPublicationWriter publicationWriter) : IDerivativesMarketDataStore
{
    public Task<DerivativeContractSynchronizationResultV1> SynchronizeContractsAsync(
        IReadOnlyCollection<CanonicalInstrumentV1> instruments,
        DateTimeOffset snapshotReceivedAtUtc,
        CancellationToken cancellationToken = default) =>
        inner.SynchronizeContractsAsync(
            instruments,
            snapshotReceivedAtUtc,
            cancellationToken);

    public Task<FuturesBasisIngestionResultV1> PersistFuturesBasisAsync(
        CanonicalFuturesBasisObservationV1 observation,
        CancellationToken cancellationToken = default) =>
        inner.PersistFuturesBasisAsync(observation, cancellationToken);

    public async Task<OptionChainIngestionResultV1> PersistOptionChainAsync(
        CanonicalOptionChainSnapshotV1 snapshot,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.PersistOptionChainAsync(snapshot, cancellationToken);
        if (!result.Outcome.Equals("CREATED", StringComparison.OrdinalIgnoreCase) ||
            result.Snapshot is null)
        {
            return result;
        }

        var contracts = await inner.GetContractsAsync(
            snapshot.UnderlyingProviderInstrumentKey,
            snapshot.ExpiryDate,
            contractClass: null,
            cancellationToken);
        var publicationEntries = new List<MarketOptionChainEntryPublishedV1>(
            result.Snapshot.Entries.Count);
        foreach (var storedEntry in result.Snapshot.Entries)
        {
            var sourceEntry = snapshot.Entries.FirstOrDefault(entry =>
                entry.StrikePrice == storedEntry.StrikePrice &&
                entry.OptionType.Equals(
                    storedEntry.OptionType,
                    StringComparison.OrdinalIgnoreCase));
            var contract = contracts.FirstOrDefault(item =>
                item.DerivativeContractUid == storedEntry.DerivativeContractUid);
            if (sourceEntry is null || contract is null)
            {
                throw new InvalidOperationException(
                    "Normalized option-chain publication lineage is incomplete.");
            }

            publicationEntries.Add(new MarketOptionChainEntryPublishedV1(
                storedEntry.DerivativeContractUid,
                sourceEntry.ProviderInstrumentKey,
                result.Snapshot.ExpiryDate,
                storedEntry.StrikePrice,
                storedEntry.OptionType,
                storedEntry.LastPrice,
                storedEntry.VolumeQuantity,
                storedEntry.OpenInterest,
                storedEntry.ImpliedVolatility,
                storedEntry.Delta,
                contract.ContractMultiplier,
                storedEntry.QualityStatus,
                storedEntry.GreeksSourceVersion));
        }

        var message = publicationFactory.CreateOptionChain(
            snapshot,
            result.Snapshot.SnapshotUid,
            result.Snapshot.SnapshotStatus,
            result.Snapshot.QualityStatus,
            result.Snapshot.IsPointInTimeEligible,
            publicationEntries);
        await publicationWriter.EnqueueAsync(message, cancellationToken);
        return result;
    }

    public Task<IReadOnlyCollection<DerivativeContractReferenceV1>> GetContractsAsync(
        string underlyingProviderInstrumentKey,
        DateOnly? expiryDate,
        string? contractClass,
        CancellationToken cancellationToken = default) =>
        inner.GetContractsAsync(
            underlyingProviderInstrumentKey,
            expiryDate,
            contractClass,
            cancellationToken);

    public Task<IReadOnlyCollection<DerivativeExpiryReferenceV1>> GetExpiriesAsync(
        string underlyingProviderInstrumentKey,
        string? marketSegment,
        CancellationToken cancellationToken = default) =>
        inner.GetExpiriesAsync(
            underlyingProviderInstrumentKey,
            marketSegment,
            cancellationToken);

    public Task<StoredFuturesBasisObservationV1?> GetLatestFuturesBasisAsync(
        string futureProviderInstrumentKey,
        DateTimeOffset? asOfUtc,
        CancellationToken cancellationToken = default) =>
        inner.GetLatestFuturesBasisAsync(
            futureProviderInstrumentKey,
            asOfUtc,
            cancellationToken);

    public Task<StoredOptionChainSnapshotV1?> GetLatestOptionChainAsync(
        string underlyingProviderInstrumentKey,
        DateOnly expiryDate,
        DateTimeOffset? asOfUtc,
        CancellationToken cancellationToken = default) =>
        inner.GetLatestOptionChainAsync(
            underlyingProviderInstrumentKey,
            expiryDate,
            asOfUtc,
            cancellationToken);
}
