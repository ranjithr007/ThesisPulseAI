using System.Globalization;
using System.Text.Json;
using ThesisPulse.Shared.Contracts.MarketData.V1;

namespace ThesisPulse.Shared.Infrastructure.MarketData;

public sealed record DerivativeContractDescriptor(
    CanonicalInstrumentV1 Instrument,
    string ContractClass,
    string ExpiryType,
    DateOnly LastTradingDate,
    DateOnly? SettlementDate,
    DateOnly? RolloverStartDate,
    string SettlementType,
    decimal ContractMultiplier,
    bool SelectionEligible);

public sealed record CalculatedFuturesBasisV1(
    decimal BasisAmount,
    decimal BasisFraction,
    int DaysToExpiry,
    decimal? AnnualizedBasisFraction,
    string QualityStatus,
    bool IsPointInTimeEligible,
    IReadOnlyCollection<string> Warnings);

public static class DerivativesMarketDataRules
{
    public static DerivativeContractDescriptor? DescribeContract(
        CanonicalInstrumentV1 instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);

        var instrumentType = instrument.InstrumentType.Trim().ToUpperInvariant();
        if (instrumentType is not ("FUTURE" or "OPTION"))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(instrument.UnderlyingProviderInstrumentKey) ||
            instrument.ExpiryDate is null)
        {
            return null;
        }

        if (instrumentType == "OPTION" &&
            (instrument.StrikePrice is null || instrument.StrikePrice <= 0 ||
             instrument.OptionType is not ("CALL" or "PUT")))
        {
            return null;
        }

        var underlyingType = ReadMetadata(instrument.Metadata, "underlyingType")
            ?? ReadMetadata(instrument.Metadata, "underlying_type")
            ?? string.Empty;
        var isIndex = underlyingType.Equals("INDEX", StringComparison.OrdinalIgnoreCase) ||
                      instrument.ProviderSegment.Contains("INDEX", StringComparison.OrdinalIgnoreCase);
        var contractClass = (isIndex, instrumentType) switch
        {
            (true, "FUTURE") => "INDEX_FUTURE",
            (true, "OPTION") => "INDEX_OPTION",
            (false, "FUTURE") => "STOCK_FUTURE",
            _ => "STOCK_OPTION",
        };

        var expiryType = NormalizeAllowed(
            ReadMetadata(instrument.Metadata, "expiryType"),
            DerivativesMarketDataContractV1.ExpiryTypes,
            fallback: IsWeekly(instrument.Metadata) ? "WEEKLY" : "UNKNOWN");
        var settlementType = NormalizeAllowed(
            ReadMetadata(instrument.Metadata, "settlementType"),
            DerivativesMarketDataContractV1.SettlementTypes,
            fallback: "UNKNOWN");
        var lastTradingDate = ReadDate(instrument.Metadata, "lastTradingDate")
            ?? instrument.ExpiryDate.Value;
        var settlementDate = ReadDate(instrument.Metadata, "settlementDate");
        var rolloverStartDate = ReadDate(instrument.Metadata, "rolloverStartDate");
        var multiplier = ReadDecimal(instrument.Metadata, "contractMultiplier")
            ?? instrument.LotSize;

        if (lastTradingDate > instrument.ExpiryDate.Value || multiplier <= 0)
        {
            return null;
        }

        return new DerivativeContractDescriptor(
            instrument,
            contractClass,
            expiryType,
            lastTradingDate,
            settlementDate,
            rolloverStartDate,
            settlementType,
            multiplier,
            SelectionEligible: false);
    }

    public static CalculatedFuturesBasisV1 CalculateBasis(
        CanonicalFuturesBasisObservationV1 observation,
        DateOnly expiryDate)
    {
        ValidateFuturesBasis(observation);
        var basisAmount = observation.FuturePrice - observation.UnderlyingPrice;
        var basisFraction = basisAmount / observation.UnderlyingPrice;
        var eventDate = DateOnly.FromDateTime(observation.EventAtUtc.UtcDateTime);
        var daysToExpiry = Math.Max(0, expiryDate.DayNumber - eventDate.DayNumber);
        var annualized = daysToExpiry > 0
            ? basisFraction * 365m / daysToExpiry
            : null;
        var warnings = new List<string>();
        var quality = MarketDataQualityStatusV1.Valid;
        var eligible = true;

        if (daysToExpiry == 0)
        {
            warnings.Add("EXPIRY_DAY_BASIS_NOT_ANNUALIZED");
        }

        if (observation.PublishedAtUtc is null)
        {
            warnings.Add("SOURCE_PUBLISHED_TIME_UNAVAILABLE");
            quality = MarketDataQualityStatusV1.Degraded;
        }

        return new CalculatedFuturesBasisV1(
            decimal.Round(basisAmount, 6, MidpointRounding.ToEven),
            decimal.Round(basisFraction, 10, MidpointRounding.ToEven),
            daysToExpiry,
            annualized is null
                ? null
                : decimal.Round(annualized.Value, 10, MidpointRounding.ToEven),
            quality,
            eligible,
            warnings);
    }

    public static void ValidateFuturesBasis(CanonicalFuturesBasisObservationV1 observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.ProviderCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.SourceEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.UnderlyingProviderInstrumentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.FutureProviderInstrumentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.SourceVersion);

        if (observation.Revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observation.Revision));
        }

        if (observation.UnderlyingProviderInstrumentKey.Equals(
                observation.FutureProviderInstrumentKey,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Underlying and future instrument keys must differ.");
        }

        if (observation.EventAtUtc == default ||
            observation.ReceivedAtUtc < observation.EventAtUtc ||
            observation.PublishedAtUtc > observation.ReceivedAtUtc)
        {
            throw new ArgumentException("Futures-basis timestamps are invalid.");
        }

        if (observation.UnderlyingPrice <= 0 || observation.FuturePrice <= 0)
        {
            throw new ArgumentException("Futures-basis prices must be positive.");
        }

        if (!Guid.TryParse(observation.CorrelationId, out _))
        {
            throw new ArgumentException("CorrelationId must be a GUID.");
        }

        ValidateJson(observation.RawPayloadJson, nameof(observation.RawPayloadJson));
    }

    public static IReadOnlyCollection<string> ValidateOptionChain(
        CanonicalOptionChainSnapshotV1 snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.ProviderCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.SourceEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.UnderlyingProviderInstrumentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.SourceVersion);

        if (snapshot.Revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot.Revision));
        }

        if (snapshot.EventAtUtc == default ||
            snapshot.ReceivedAtUtc < snapshot.EventAtUtc ||
            snapshot.PublishedAtUtc > snapshot.ReceivedAtUtc)
        {
            throw new ArgumentException("Option-chain timestamps are invalid.");
        }

        if (snapshot.UnderlyingPrice <= 0)
        {
            throw new ArgumentException("Option-chain underlying price must be positive.");
        }

        if (!Guid.TryParse(snapshot.CorrelationId, out _))
        {
            throw new ArgumentException("CorrelationId must be a GUID.");
        }

        if (snapshot.Entries.Count == 0)
        {
            throw new ArgumentException("Option-chain snapshot requires at least one entry.");
        }

        ValidateJson(snapshot.RawPayloadJson, nameof(snapshot.RawPayloadJson));

        var warnings = new List<string>();
        var duplicateKeys = snapshot.Entries
            .GroupBy(entry => entry.ProviderInstrumentKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateKeys.Length > 0)
        {
            throw new ArgumentException(
                $"Option-chain snapshot contains duplicate contracts: {string.Join(", ", duplicateKeys)}");
        }

        foreach (var entry in snapshot.Entries)
        {
            var reason = ValidateOptionEntry(entry, snapshot);
            if (reason is not null)
            {
                warnings.Add($"{entry.ProviderInstrumentKey}: {reason}");
            }
        }

        return warnings;
    }

    public static string? ValidateOptionEntry(
        CanonicalOptionChainEntryV1 entry,
        CanonicalOptionChainSnapshotV1 snapshot)
    {
        if (string.IsNullOrWhiteSpace(entry.ProviderInstrumentKey))
        {
            return "provider instrument key is required";
        }

        if (entry.QuoteAtUtc == default || entry.QuoteAtUtc > snapshot.ReceivedAtUtc)
        {
            return "quote timestamp is invalid";
        }

        if (entry.StrikePrice <= 0 || entry.OptionType is not ("CALL" or "PUT"))
        {
            return "strike and option type are invalid";
        }

        if (AnyNegative(
                entry.BidPrice,
                entry.AskPrice,
                entry.LastPrice,
                entry.BidQuantity,
                entry.AskQuantity,
                entry.VolumeQuantity,
                entry.OpenInterest,
                entry.PreviousOpenInterest,
                entry.ImpliedVolatility,
                entry.Gamma))
        {
            return "market values cannot be negative";
        }

        if (entry.BidPrice is not null && entry.AskPrice is not null &&
            entry.AskPrice < entry.BidPrice)
        {
            return "ask price cannot be below bid price";
        }

        if (entry.Delta is < -1m or > 1m)
        {
            return "delta must be between -1 and 1";
        }

        if (entry.QualityStatus is not (
            MarketDataQualityStatusV1.Valid or
            MarketDataQualityStatusV1.Degraded or
            MarketDataQualityStatusV1.Stale or
            MarketDataQualityStatusV1.Incomplete or
            MarketDataQualityStatusV1.Invalid))
        {
            return "quality status is unsupported";
        }

        return null;
    }

    private static bool AnyNegative(params decimal?[] values) =>
        values.Any(value => value is < 0m);

    private static string? ReadMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        string key)
    {
        if (metadata is null)
        {
            return null;
        }

        foreach (var pair in metadata)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static bool IsWeekly(IReadOnlyDictionary<string, string>? metadata) =>
        bool.TryParse(ReadMetadata(metadata, "weekly"), out var weekly) && weekly;

    private static string NormalizeAllowed(
        string? value,
        IReadOnlySet<string> allowed,
        string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Trim().ToUpperInvariant();
        return allowed.Contains(normalized) ? normalized : fallback;
    }

    private static DateOnly? ReadDate(
        IReadOnlyDictionary<string, string>? metadata,
        string key) =>
        DateOnly.TryParse(
            ReadMetadata(metadata, key),
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;

    private static decimal? ReadDecimal(
        IReadOnlyDictionary<string, string>? metadata,
        string key) =>
        decimal.TryParse(
            ReadMetadata(metadata, key),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private static void ValidateJson(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Raw payload JSON is required.", parameterName);
        }

        try
        {
            using var _ = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Raw payload must contain valid JSON.", parameterName, exception);
        }
    }
}
