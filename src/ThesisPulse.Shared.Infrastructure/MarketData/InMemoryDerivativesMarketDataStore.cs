using System.Security.Cryptography;
using System.Text;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed class InMemoryDerivativesMarketDataStore : IDerivativesMarketDataStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, CanonicalInstrumentV1> _instruments =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ContractState> _contracts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExpiryState> _expiries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StoredFuturesBasisObservationV1> _basisBySource =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<StoredFuturesBasisObservationV1>> _basisByFuture =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StoredOptionChainSnapshotV1> _chainsBySource =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<StoredOptionChainSnapshotV1>> _chainsByScope =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<DerivativeContractSynchronizationResultV1> SynchronizeContractsAsync(
        IReadOnlyCollection<CanonicalInstrumentV1> instruments,
        DateTimeOffset snapshotReceivedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        var created = 0;
        var updated = 0;
        var expiryCreated = 0;
        var expiryUpdated = 0;
        var skipped = 0;
        var warnings = new List<string>();

        lock (_sync)
        {
            foreach (var instrument in instruments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _instruments[instrument.ProviderInstrumentKey] = instrument;
            }

            foreach (var instrument in instruments)
            {
                var descriptor = DerivativesMarketDataRules.DescribeContract(instrument);
                if (descriptor is null)
                {
                    if (instrument.InstrumentType is "FUTURE" or "OPTION")
                    {
                        skipped++;
                        warnings.Add(
                            $"Skipped '{instrument.ProviderInstrumentKey}' because canonical " +
                            "derivative metadata was incomplete.");
                    }
                    continue;
                }

                if (!_instruments.TryGetValue(
                        instrument.UnderlyingProviderInstrumentKey!,
                        out var underlying))
                {
                    skipped++;
                    warnings.Add(
                        $"Skipped '{instrument.ProviderInstrumentKey}' because underlying " +
                        $"'{instrument.UnderlyingProviderInstrumentKey}' was unavailable.");
                    continue;
                }

                var state = new ContractState(
                    descriptor,
                    underlying,
                    StableGuid($"derivative-contract|{instrument.ProviderCode}|" +
                               $"{instrument.ProviderInstrumentKey}|{instrument.EffectiveFromDate:O}"),
                    StableGuid($"instrument|{instrument.ProviderCode}|" +
                               instrument.ProviderInstrumentKey),
                    StableGuid($"instrument|{underlying.ProviderCode}|" +
                               underlying.ProviderInstrumentKey));
                if (_contracts.ContainsKey(instrument.ProviderInstrumentKey))
                {
                    updated++;
                }
                else
                {
                    created++;
                }
                _contracts[instrument.ProviderInstrumentKey] = state;

                var segment = instrument.InstrumentType == "FUTURE" ? "FUTURES" : "OPTIONS";
                var expiryKey = ExpiryKey(
                    instrument.UnderlyingProviderInstrumentKey!,
                    segment,
                    instrument.ExpiryDate!.Value);
                var expiryState = new ExpiryState(
                    underlying,
                    StableGuid($"derivative-expiry|{underlying.ProviderCode}|" +
                               $"{underlying.ProviderInstrumentKey}|{segment}|" +
                               $"{instrument.ExpiryDate:yyyy-MM-dd}"),
                    StableGuid($"instrument|{underlying.ProviderCode}|" +
                               underlying.ProviderInstrumentKey),
                    instrument.ExchangeCode,
                    segment,
                    instrument.ExpiryDate.Value,
                    descriptor.ExpiryType,
                    descriptor.LastTradingDate,
                    descriptor.SettlementDate,
                    descriptor.RolloverStartDate,
                    instrument.ExpiryDate.Value >= DateOnly.FromDateTime(
                        snapshotReceivedAtUtc.UtcDateTime)
                        ? "SCHEDULED"
                        : "EXPIRED",
                    DerivativesMarketDataContractV1.CatalogPolicyVersion,
                    instrument.EffectiveFromDate);
                if (_expiries.ContainsKey(expiryKey))
                {
                    expiryUpdated++;
                }
                else
                {
                    expiryCreated++;
                }
                _expiries[expiryKey] = expiryState;
            }
        }

        return Task.FromResult(new DerivativeContractSynchronizationResultV1(
            instruments.FirstOrDefault()?.ProviderCode ?? "UNKNOWN",
            snapshotReceivedAtUtc,
            instruments.Count(item => item.InstrumentType is "FUTURE" or "OPTION"),
            created,
            updated,
            expiryCreated,
            expiryUpdated,
            skipped,
            warnings));
    }

    public Task<FuturesBasisIngestionResultV1> PersistFuturesBasisAsync(
        CanonicalFuturesBasisObservationV1 observation,
        CancellationToken cancellationToken = default)
    {
        DerivativesMarketDataRules.ValidateFuturesBasis(observation);
        var sourceKey = SourceKey(
            observation.ProviderCode,
            observation.SourceEventId,
            observation.Revision);

        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_basisBySource.TryGetValue(sourceKey, out var duplicate))
            {
                return Task.FromResult(new FuturesBasisIngestionResultV1(
                    "DUPLICATE",
                    duplicate,
                    Array.Empty<string>()));
            }

            if (!_contracts.TryGetValue(
                    observation.FutureProviderInstrumentKey,
                    out var contract) ||
                contract.Descriptor.Instrument.InstrumentType != "FUTURE")
            {
                throw new KeyNotFoundException(
                    "The canonical future contract is not synchronized.");
            }

            if (!contract.Descriptor.Instrument.UnderlyingProviderInstrumentKey!.Equals(
                    observation.UnderlyingProviderInstrumentKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Futures-basis underlying lineage does not match the contract catalog.");
            }

            var calculated = DerivativesMarketDataRules.CalculateBasis(
                observation,
                contract.Descriptor.Instrument.ExpiryDate!.Value);
            var stored = new StoredFuturesBasisObservationV1(
                StableGuid($"futures-basis|{sourceKey}"),
                contract.UnderlyingInstrumentUid,
                contract.Underlying.CanonicalSymbol,
                contract.InstrumentUid,
                contract.Descriptor.Instrument.CanonicalSymbol,
                contract.ContractUid,
                contract.Descriptor.Instrument.ExpiryDate.Value,
                observation.EventAtUtc,
                observation.PublishedAtUtc,
                observation.ReceivedAtUtc,
                observation.UnderlyingPrice,
                observation.FuturePrice,
                calculated.BasisAmount,
                calculated.BasisFraction,
                calculated.DaysToExpiry,
                calculated.AnnualizedBasisFraction,
                calculated.QualityStatus,
                calculated.IsPointInTimeEligible,
                observation.Revision,
                observation.SourceVersion,
                DerivativesMarketDataContractV1.BasisPolicyVersion);
            _basisBySource[sourceKey] = stored;
            if (!_basisByFuture.TryGetValue(
                    observation.FutureProviderInstrumentKey,
                    out var observations))
            {
                observations = new List<StoredFuturesBasisObservationV1>();
                _basisByFuture[observation.FutureProviderInstrumentKey] = observations;
            }
            observations.Add(stored);
            return Task.FromResult(new FuturesBasisIngestionResultV1(
                "CREATED",
                stored,
                calculated.Warnings));
        }
    }

    public Task<OptionChainIngestionResultV1> PersistOptionChainAsync(
        CanonicalOptionChainSnapshotV1 snapshot,
        CancellationToken cancellationToken = default)
    {
        var validationWarnings = DerivativesMarketDataRules.ValidateOptionChain(snapshot);
        var sourceKey = SourceKey(
            snapshot.ProviderCode,
            snapshot.SourceEventId,
            snapshot.Revision);

        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_chainsBySource.TryGetValue(sourceKey, out var duplicate))
            {
                return Task.FromResult(new OptionChainIngestionResultV1(
                    "DUPLICATE",
                    duplicate,
                    snapshot.Entries.Count,
                    duplicate.Entries.Count,
                    snapshot.Entries.Count - duplicate.Entries.Count,
                    duplicate.Warnings));
            }

            if (!_instruments.TryGetValue(
                    snapshot.UnderlyingProviderInstrumentKey,
                    out var underlying))
            {
                throw new KeyNotFoundException(
                    "The option-chain underlying instrument is not synchronized.");
            }

            var accepted = new List<StoredOptionChainEntryV1>();
            var warnings = new List<string>(validationWarnings);
            foreach (var entry in snapshot.Entries)
            {
                var validationReason = DerivativesMarketDataRules.ValidateOptionEntry(
                    entry,
                    snapshot);
                if (validationReason is not null)
                {
                    warnings.Add($"{entry.ProviderInstrumentKey}: {validationReason}");
                    continue;
                }

                if (!_contracts.TryGetValue(entry.ProviderInstrumentKey, out var contract) ||
                    contract.Descriptor.Instrument.InstrumentType != "OPTION")
                {
                    warnings.Add(
                        $"{entry.ProviderInstrumentKey}: canonical option contract not found");
                    continue;
                }

                var instrument = contract.Descriptor.Instrument;
                if (!instrument.UnderlyingProviderInstrumentKey!.Equals(
                        snapshot.UnderlyingProviderInstrumentKey,
                        StringComparison.OrdinalIgnoreCase) ||
                    instrument.ExpiryDate != snapshot.ExpiryDate ||
                    instrument.StrikePrice != entry.StrikePrice ||
                    !string.Equals(
                        instrument.OptionType,
                        entry.OptionType,
                        StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(
                        $"{entry.ProviderInstrumentKey}: option contract lineage mismatch");
                    continue;
                }

                accepted.Add(new StoredOptionChainEntryV1(
                    contract.ContractUid,
                    contract.InstrumentUid,
                    instrument.CanonicalSymbol,
                    entry.QuoteAtUtc,
                    entry.StrikePrice,
                    entry.OptionType,
                    entry.BidPrice,
                    entry.AskPrice,
                    entry.LastPrice,
                    entry.BidQuantity,
                    entry.AskQuantity,
                    entry.VolumeQuantity,
                    entry.OpenInterest,
                    entry.PreviousOpenInterest,
                    entry.OpenInterest is not null && entry.PreviousOpenInterest is not null
                        ? entry.OpenInterest - entry.PreviousOpenInterest
                        : null,
                    entry.ImpliedVolatility,
                    entry.Delta,
                    entry.Gamma,
                    entry.Theta,
                    entry.Vega,
                    entry.Rho,
                    entry.GreeksSourceVersion,
                    entry.QualityStatus,
                    entry.Metadata));
            }

            var status = accepted.Count == snapshot.Entries.Count
                ? "COMPLETE"
                : accepted.Count == 0
                    ? "INVALID"
                    : "PARTIAL";
            var quality = status switch
            {
                "COMPLETE" => MarketDataQualityStatusV1.Valid,
                "PARTIAL" => MarketDataQualityStatusV1.Degraded,
                _ => MarketDataQualityStatusV1.Invalid,
            };
            var stored = new StoredOptionChainSnapshotV1(
                StableGuid($"option-chain|{sourceKey}"),
                StableGuid($"instrument|{underlying.ProviderCode}|" +
                           underlying.ProviderInstrumentKey),
                underlying.CanonicalSymbol,
                snapshot.ExpiryDate,
                snapshot.EventAtUtc,
                snapshot.PublishedAtUtc,
                snapshot.ReceivedAtUtc,
                snapshot.UnderlyingPrice,
                status,
                quality,
                status == "COMPLETE",
                snapshot.Revision,
                snapshot.SourceVersion,
                snapshot.CalculationSourceVersion,
                DerivativesMarketDataContractV1.OptionChainPolicyVersion,
                accepted,
                warnings);
            _chainsBySource[sourceKey] = stored;
            var scopeKey = ChainScopeKey(
                snapshot.UnderlyingProviderInstrumentKey,
                snapshot.ExpiryDate);
            if (!_chainsByScope.TryGetValue(scopeKey, out var snapshots))
            {
                snapshots = new List<StoredOptionChainSnapshotV1>();
                _chainsByScope[scopeKey] = snapshots;
            }
            snapshots.Add(stored);
            return Task.FromResult(new OptionChainIngestionResultV1(
                "CREATED",
                stored,
                snapshot.Entries.Count,
                accepted.Count,
                snapshot.Entries.Count - accepted.Count,
                warnings));
        }
    }

    public Task<IReadOnlyCollection<DerivativeContractReferenceV1>> GetContractsAsync(
        string underlyingProviderInstrumentKey,
        DateOnly? expiryDate,
        string? contractClass,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlyingProviderInstrumentKey);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = _contracts.Values
                .Where(state => state.Descriptor.Instrument
                    .UnderlyingProviderInstrumentKey!.Equals(
                        underlyingProviderInstrumentKey,
                        StringComparison.OrdinalIgnoreCase))
                .Where(state => expiryDate is null ||
                    state.Descriptor.Instrument.ExpiryDate == expiryDate)
                .Where(state => string.IsNullOrWhiteSpace(contractClass) ||
                    state.Descriptor.ContractClass.Equals(
                        contractClass,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(state => state.Descriptor.Instrument.ExpiryDate)
                .ThenBy(state => state.Descriptor.Instrument.StrikePrice)
                .ThenBy(state => state.Descriptor.Instrument.OptionType)
                .Select(ToReference)
                .ToArray();
            return Task.FromResult<IReadOnlyCollection<DerivativeContractReferenceV1>>(values);
        }
    }

    public Task<IReadOnlyCollection<DerivativeExpiryReferenceV1>> GetExpiriesAsync(
        string underlyingProviderInstrumentKey,
        string? marketSegment,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlyingProviderInstrumentKey);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = _expiries.Values
                .Where(state => state.Underlying.ProviderInstrumentKey.Equals(
                    underlyingProviderInstrumentKey,
                    StringComparison.OrdinalIgnoreCase))
                .Where(state => string.IsNullOrWhiteSpace(marketSegment) ||
                    state.MarketSegment.Equals(
                        marketSegment,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(state => state.ExpiryDate)
                .Select(ToReference)
                .ToArray();
            return Task.FromResult<IReadOnlyCollection<DerivativeExpiryReferenceV1>>(values);
        }
    }

    public Task<StoredFuturesBasisObservationV1?> GetLatestFuturesBasisAsync(
        string futureProviderInstrumentKey,
        DateTimeOffset? asOfUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(futureProviderInstrumentKey);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_basisByFuture.TryGetValue(futureProviderInstrumentKey, out var values))
            {
                return Task.FromResult<StoredFuturesBasisObservationV1?>(null);
            }

            var cutoff = asOfUtc ?? DateTimeOffset.MaxValue;
            return Task.FromResult(values
                .Where(value => value.EventAtUtc <= cutoff && value.ReceivedAtUtc <= cutoff)
                .OrderByDescending(value => value.EventAtUtc)
                .ThenByDescending(value => value.Revision)
                .FirstOrDefault());
        }
    }

    public Task<StoredOptionChainSnapshotV1?> GetLatestOptionChainAsync(
        string underlyingProviderInstrumentKey,
        DateOnly expiryDate,
        DateTimeOffset? asOfUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlyingProviderInstrumentKey);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_chainsByScope.TryGetValue(
                    ChainScopeKey(underlyingProviderInstrumentKey, expiryDate),
                    out var values))
            {
                return Task.FromResult<StoredOptionChainSnapshotV1?>(null);
            }

            var cutoff = asOfUtc ?? DateTimeOffset.MaxValue;
            return Task.FromResult(values
                .Where(value => value.EventAtUtc <= cutoff && value.ReceivedAtUtc <= cutoff)
                .OrderByDescending(value => value.EventAtUtc)
                .ThenByDescending(value => value.Revision)
                .FirstOrDefault());
        }
    }

    private static DerivativeContractReferenceV1 ToReference(ContractState state) =>
        new(
            state.ContractUid,
            state.InstrumentUid,
            state.Descriptor.Instrument.CanonicalSymbol,
            state.UnderlyingInstrumentUid,
            state.Underlying.CanonicalSymbol,
            state.Descriptor.ContractClass,
            state.Descriptor.Instrument.ExpiryDate!.Value,
            state.Descriptor.ExpiryType,
            state.Descriptor.LastTradingDate,
            state.Descriptor.SettlementDate,
            state.Descriptor.RolloverStartDate,
            state.Descriptor.SettlementType,
            state.Descriptor.ContractMultiplier,
            state.Descriptor.Instrument.LotSize,
            state.Descriptor.Instrument.StrikePrice,
            state.Descriptor.Instrument.OptionType,
            "ACTIVE",
            state.Descriptor.SelectionEligible,
            state.Descriptor.Instrument.EffectiveFromDate,
            null,
            state.Descriptor.Instrument.Metadata);

    private static DerivativeExpiryReferenceV1 ToReference(ExpiryState state) =>
        new(
            state.ExpiryUid,
            state.UnderlyingInstrumentUid,
            state.Underlying.CanonicalSymbol,
            state.ExchangeCode,
            state.MarketSegment,
            state.ExpiryDate,
            state.ExpiryType,
            state.LastTradingDate,
            state.SettlementDate,
            state.RolloverStartDate,
            state.Status,
            state.CalendarVersion,
            state.ValidFromDate,
            null);

    private static string SourceKey(string provider, string sourceEventId, int revision) =>
        $"{provider}|{sourceEventId}|{revision}";

    private static string ExpiryKey(string underlying, string segment, DateOnly expiry) =>
        $"{underlying}|{segment}|{expiry:yyyy-MM-dd}";

    private static string ChainScopeKey(string underlying, DateOnly expiry) =>
        $"{underlying}|{expiry:yyyy-MM-dd}";

    private static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private sealed record ContractState(
        DerivativeContractDescriptor Descriptor,
        CanonicalInstrumentV1 Underlying,
        Guid ContractUid,
        Guid InstrumentUid,
        Guid UnderlyingInstrumentUid);

    private sealed record ExpiryState(
        CanonicalInstrumentV1 Underlying,
        Guid ExpiryUid,
        Guid UnderlyingInstrumentUid,
        string ExchangeCode,
        string MarketSegment,
        DateOnly ExpiryDate,
        string ExpiryType,
        DateOnly LastTradingDate,
        DateOnly? SettlementDate,
        DateOnly? RolloverStartDate,
        string Status,
        string CalendarVersion,
        DateOnly ValidFromDate);
}
