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
        var type = instrument.InstrumentType.Trim().ToUpperInvariant();
        if (type is not ("FUTURE" or "OPTION") ||
            string.IsNullOrWhiteSpace(instrument.UnderlyingProviderInstrumentKey) ||
            instrument.ExpiryDate is null)
        {
            return null;
        }

        if (type == "OPTION" &&
            (instrument.StrikePrice is null or <= 0 ||
             instrument.OptionType is not ("CALL" or "PUT")))
        {
            return null;
        }

        var underlyingType = Read(instrument.Metadata, "underlyingType")
            ?? Read(instrument.Metadata, "underlying_type")
            ?? string.Empty;
        var isIndex = underlyingType.Equals("INDEX", StringComparison.OrdinalIgnoreCase) ||
            instrument.ProviderSegment.Contains("INDEX", StringComparison.OrdinalIgnoreCase);
        var contractClass = (isIndex, type) switch
        {
            (true, "FUTURE") => "INDEX_FUTURE",
            (true, "OPTION") => "INDEX_OPTION",
            (false, "FUTURE") => "STOCK_FUTURE",
            _ => "STOCK_OPTION",
        };
        var expiryType = Allowed(
            Read(instrument.Metadata, "expiryType"),
            DerivativesMarketDataContractV1.ExpiryTypes,
            IsWeekly(instrument.Metadata) ? "WEEKLY" : "UNKNOWN");
        var settlementType = Allowed(
            Read(instrument.Metadata, "settlementType"),
            DerivativesMarketDataContractV1.SettlementTypes,
            "UNKNOWN");
        var lastTradingDate = ReadDate(instrument.Metadata, "lastTradingDate")
            ?? instrument.ExpiryDate.Value;
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
            ReadDate(instrument.Metadata, "settlementDate"),
            ReadDate(instrument.Metadata, "rolloverStartDate"),
            settlementType,
            multiplier,
            SelectionEligible: false);
    }

    public static CalculatedFuturesBasisV1 CalculateBasis(
        CanonicalFuturesBasisObservationV1 observation,
        DateOnly expiryDate)
    {
        ValidateFuturesBasis(observation);
        var amount = observation.FuturePrice - observation.UnderlyingPrice;
        var fraction = amount / observation.UnderlyingPrice;
        var eventDate = DateOnly.FromDateTime(observation.EventAtUtc.UtcDateTime);
        var days = Math.Max(0, expiryDate.DayNumber - eventDate.DayNumber);
        decimal? annualized = days > 0 ? fraction * 365m / days : null;
        var warnings = new List<string>();
        var quality = MarketDataQualityStatusV1.Valid;
        if (days == 0)
        {
            warnings.Add("EXPIRY_DAY_BASIS_NOT_ANNUALIZED");
        }
        if (observation.PublishedAtUtc is null)
        {
            warnings.Add("SOURCE_PUBLISHED_TIME_UNAVAILABLE");
            quality = MarketDataQualityStatusV1.Degraded;
        }

        return new CalculatedFuturesBasisV1(
            decimal.Round(amount, 6, MidpointRounding.ToEven),
            decimal.Round(fraction, 10, MidpointRounding.ToEven),
            days,
            annualized is null
                ? null
                : decimal.Round(annualized.Value, 10, MidpointRounding.ToEven),
            quality,
            IsPointInTimeEligible: true,
            warnings);
    }

    public static void ValidateFuturesBasis(CanonicalFuturesBasisObservationV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Required(value.ProviderCode, nameof(value.ProviderCode));
        Required(value.SourceEventId, nameof(value.SourceEventId));
        Required(value.UnderlyingProviderInstrumentKey,
            nameof(value.UnderlyingProviderInstrumentKey));
        Required(value.FutureProviderInstrumentKey,
            nameof(value.FutureProviderInstrumentKey));
        Required(value.SourceVersion, nameof(value.SourceVersion));
        if (value.Revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value.Revision));
        }
        if (value.UnderlyingProviderInstrumentKey.Equals(
                value.FutureProviderInstrumentKey,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Underlying and future instrument keys must differ.");
        }
        ValidateTimes(value.EventAtUtc, value.PublishedAtUtc, value.ReceivedAtUtc);
        if (value.UnderlyingPrice <= 0 || value.FuturePrice <= 0)
        {
            throw new ArgumentException("Futures-basis prices must be positive.");
        }
        ValidateCorrelation(value.CorrelationId);
        ValidateJson(value.RawPayloadJson, nameof(value.RawPayloadJson));
    }

    public static IReadOnlyCollection<string> ValidateOptionChain(
        CanonicalOptionChainSnapshotV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Required(value.ProviderCode, nameof(value.ProviderCode));
        Required(value.SourceEventId, nameof(value.SourceEventId));
        Required(value.UnderlyingProviderInstrumentKey,
            nameof(value.UnderlyingProviderInstrumentKey));
        Required(value.SourceVersion, nameof(value.SourceVersion));
        if (value.Revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value.Revision));
        }
        ValidateTimes(value.EventAtUtc, value.PublishedAtUtc, value.ReceivedAtUtc);
        if (value.UnderlyingPrice <= 0 || value.Entries.Count == 0)
        {
            throw new ArgumentException(
                "Option-chain requires a positive underlying price and at least one entry.");
        }
        ValidateCorrelation(value.CorrelationId);
        ValidateJson(value.RawPayloadJson, nameof(value.RawPayloadJson));
        var duplicates = value.Entries
            .GroupBy(entry => entry.ProviderInstrumentKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new ArgumentException(
                $"Option-chain snapshot contains duplicate contracts: {string.Join(", ", duplicates)}");
        }

        return value.Entries
            .Select(entry => (entry.ProviderInstrumentKey, Reason: ValidateOptionEntry(entry, value)))
            .Where(item => item.Reason is not null)
            .Select(item => $"{item.ProviderInstrumentKey}: {item.Reason}")
            .ToArray();
    }

    public static string? ValidateOptionEntry(
        CanonicalOptionChainEntryV1 entry,
        CanonicalOptionChainSnapshotV1 snapshot)
    {
        if (string.IsNullOrWhiteSpace(entry.ProviderInstrumentKey))
            return "provider instrument key is required";
        if (entry.QuoteAtUtc == default || entry.QuoteAtUtc > snapshot.ReceivedAtUtc)
            return "quote timestamp is invalid";
        if (entry.StrikePrice <= 0 || entry.OptionType is not ("CALL" or "PUT"))
            return "strike and option type are invalid";
        if (AnyNegative(entry.BidPrice, entry.AskPrice, entry.LastPrice,
                entry.BidQuantity, entry.AskQuantity, entry.VolumeQuantity,
                entry.OpenInterest, entry.PreviousOpenInterest,
                entry.ImpliedVolatility, entry.Gamma))
            return "market values cannot be negative";
        if (entry.BidPrice is not null && entry.AskPrice is not null &&
            entry.AskPrice < entry.BidPrice)
            return "ask price cannot be below bid price";
        if (entry.Delta is < -1m or > 1m)
            return "delta must be between -1 and 1";
        if (entry.QualityStatus is not (
            MarketDataQualityStatusV1.Valid or
            MarketDataQualityStatusV1.Degraded or
            MarketDataQualityStatusV1.Stale or
            MarketDataQualityStatusV1.Incomplete or
            MarketDataQualityStatusV1.Invalid))
            return "quality status is unsupported";
        return null;
    }

    private static void ValidateTimes(
        DateTimeOffset eventAt,
        DateTimeOffset? publishedAt,
        DateTimeOffset receivedAt)
    {
        if (eventAt == default || receivedAt < eventAt || publishedAt > receivedAt)
        {
            throw new ArgumentException("Derivatives timestamps are invalid.");
        }
    }

    private static void ValidateCorrelation(string value)
    {
        if (!Guid.TryParse(value, out _))
            throw new ArgumentException("CorrelationId must be a GUID.");
    }

    private static void Required(string value, string name) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);

    private static bool AnyNegative(params decimal?[] values) =>
        values.Any(value => value is < 0m);

    private static string? Read(
        IReadOnlyDictionary<string, string>? metadata,
        string key) =>
        metadata?.FirstOrDefault(pair =>
            pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool IsWeekly(IReadOnlyDictionary<string, string>? metadata) =>
        bool.TryParse(Read(metadata, "weekly"), out var weekly) && weekly;

    private static string Allowed(
        string? value,
        IReadOnlySet<string> allowed,
        string fallback)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized is not null && allowed.Contains(normalized)
            ? normalized
            : fallback;
    }

    private static DateOnly? ReadDate(
        IReadOnlyDictionary<string, string>? metadata,
        string key) =>
        DateOnly.TryParse(
            Read(metadata, key),
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;

    private static decimal? ReadDecimal(
        IReadOnlyDictionary<string, string>? metadata,
        string key) =>
        decimal.TryParse(
            Read(metadata, key),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private static void ValidateJson(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Raw payload JSON is required.", name);
        try
        {
            using var _ = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Raw payload must contain valid JSON.", name, exception);
        }
    }
}
