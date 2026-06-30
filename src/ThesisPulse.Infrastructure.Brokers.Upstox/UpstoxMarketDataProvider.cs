using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.MarketData;

namespace ThesisPulse.Infrastructure.Brokers.Upstox;

public sealed class UpstoxMarketDataProvider(
    HttpClient httpClient,
    UpstoxMarketDataOptions options,
    IUpstoxCredentialProvider credentialProvider) : IMarketDataProvider
{
    public string ProviderCode => "UPSTOX";

    public async Task<IReadOnlyCollection<CanonicalInstrumentV1>> GetInstrumentSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            options.InstrumentMasterUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        await using var contentStream = IsGzipResponse(response)
            ? new GZipStream(responseStream, CompressionMode.Decompress)
            : responseStream;
        using var document = await JsonDocument.ParseAsync(
            contentStream,
            cancellationToken: cancellationToken);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Upstox instrument snapshot root must be a JSON array.");
        }

        var effectiveDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var instruments = new List<CanonicalInstrumentV1>();

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (instruments.Count >= options.MaximumInstrumentCount)
            {
                throw new InvalidOperationException(
                    "Upstox instrument snapshot exceeded the configured maximum.");
            }

            var instrument = TryMapInstrument(item, effectiveDate);
            if (instrument is not null)
            {
                instruments.Add(instrument);
            }
        }

        return instruments;
    }

    public async Task<IReadOnlyCollection<CanonicalCandleV1>> GetHistoricalCandlesAsync(
        HistoricalCandleRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ValidateHistoricalRequest(request);
        var (unit, interval) = MapTimeframe(request.Timeframe);
        var path = options.HistoricalPathTemplate
            .Replace(
                "{instrumentKey}",
                Uri.EscapeDataString(request.ProviderInstrumentKey),
                StringComparison.Ordinal)
            .Replace("{unit}", unit, StringComparison.Ordinal)
            .Replace("{interval}", interval, StringComparison.Ordinal)
            .Replace(
                "{toDate}",
                request.ToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace(
                "{fromDate}",
                request.FromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, path);
        await AddAuthorizationAsync(httpRequest, cancellationToken);
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();
        var receivedAtUtc = DateTimeOffset.UtcNow;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        if (!TryGetProperty(document.RootElement, "data", out var data) ||
            !TryGetProperty(data, "candles", out var candleArray) ||
            candleArray.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Upstox historical response did not contain data.candles.");
        }

        var duration = MarketDataContractV1.GetDuration(request.Timeframe);
        var candles = new List<CanonicalCandleV1>();

        foreach (var candleElement in candleArray.EnumerateArray())
        {
            if (candleElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var fields = candleElement.EnumerateArray().ToArray();
            if (fields.Length < 6 ||
                !TryReadTimestamp(fields[0], out var openAtUtc) ||
                !TryReadDecimal(fields[1], out var openPrice) ||
                !TryReadDecimal(fields[2], out var highPrice) ||
                !TryReadDecimal(fields[3], out var lowPrice) ||
                !TryReadDecimal(fields[4], out var closePrice) ||
                !TryReadDecimal(fields[5], out var volumeQuantity))
            {
                continue;
            }

            decimal? openInterest = null;
            if (fields.Length > 6 && TryReadDecimal(fields[6], out var parsedOi))
            {
                openInterest = parsedOi;
            }

            var closeAtUtc = openAtUtc.Add(duration);
            var rawPayload = candleElement.GetRawText();
            candles.Add(new CanonicalCandleV1(
                ProviderCode,
                request.ProviderInstrumentKey,
                BuildCandleEventId(
                    request.ProviderInstrumentKey,
                    request.Timeframe,
                    openAtUtc),
                request.Timeframe,
                openAtUtc,
                closeAtUtc,
                openPrice,
                highPrice,
                lowPrice,
                closePrice,
                volumeQuantity,
                openInterest,
                TradeCount: null,
                VwapPrice: null,
                IsClosed: closeAtUtc <= receivedAtUtc,
                PublishedAtUtc: null,
                receivedAtUtc,
                SourceVersion: "upstox-historical-v3",
                rawPayload));
        }

        return candles;
    }

    public async Task<Uri> GetLiveFeedAuthorizedUriAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            options.MarketFeedAuthorizePath);
        await AddAuthorizationAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        if (!TryGetProperty(document.RootElement, "data", out var data) ||
            !TryGetString(data, "authorized_redirect_uri", out var uriValue) ||
            !Uri.TryCreate(uriValue, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                "Upstox market-feed authorization response did not contain a valid URI.");
        }

        return uri;
    }

    private CanonicalInstrumentV1? TryMapInstrument(
        JsonElement item,
        DateOnly effectiveDate)
    {
        if (!TryGetString(item, "instrument_key", out var instrumentKey) ||
            !TryGetString(item, "instrument_type", out var providerInstrumentType))
        {
            return null;
        }

        var classification = MapInstrumentType(providerInstrumentType);
        if (classification is null)
        {
            return null;
        }

        var canonicalSymbol = GetFirstString(
            item,
            "trading_symbol",
            "short_name",
            "name");
        var displayName = GetFirstString(
            item,
            "name",
            "short_name",
            "trading_symbol");
        var providerSegment = GetFirstString(item, "segment") ?? "UNKNOWN";
        var exchangeCode = MapExchangeCode(
            GetFirstString(item, "exchange", "exchange_code"),
            providerSegment);

        if (string.IsNullOrWhiteSpace(canonicalSymbol) ||
            string.IsNullOrWhiteSpace(displayName) ||
            string.IsNullOrWhiteSpace(exchangeCode) ||
            canonicalSymbol.Length > 100 ||
            displayName.Length > 200)
        {
            return null;
        }

        var rawTickSize = ReadOptionalDecimal(item, "tick_size") ?? 5m;
        var tickSize = rawTickSize / options.TickSizeDivisor;
        var lotSize = ReadOptionalDecimal(item, "lot_size") ?? 1m;
        var freezeQuantity = ReadOptionalDecimal(item, "freeze_quantity");
        var expiryDate = ReadOptionalDate(item, "expiry");
        var strikePrice = ReadOptionalDecimal(item, "strike_price");
        var optionType = classification.Value.InstrumentType == "OPTION"
            ? providerInstrumentType.Equals("CE", StringComparison.OrdinalIgnoreCase)
                ? "CALL"
                : "PUT"
            : null;
        var metadata = new Dictionary<string, string>
        {
            ["providerInstrumentType"] = providerInstrumentType,
            ["providerSegment"] = providerSegment,
        };

        AddMetadata(item, metadata, "exchange_token");
        AddMetadata(item, metadata, "security_type");
        AddMetadata(item, metadata, "underlying_type");

        return new CanonicalInstrumentV1(
            ProviderCode,
            instrumentKey,
            exchangeCode,
            providerSegment,
            canonicalSymbol,
            displayName,
            classification.Value.InstrumentType,
            classification.Value.MarketSegment,
            GetFirstString(item, "isin"),
            GetFirstString(item, "underlying_key"),
            expiryDate,
            classification.Value.InstrumentType == "OPTION" ? strikePrice : null,
            optionType,
            tickSize > 0 ? tickSize : 0.05m,
            lotSize > 0 ? lotSize : 1m,
            freezeQuantity,
            IsTradeAllowed: false,
            IsShortAllowed: false,
            effectiveDate,
            metadata);
    }

    private async Task AddAuthorizationAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await credentialProvider.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static bool IsGzipResponse(HttpResponseMessage response) =>
        response.Content.Headers.ContentEncoding.Any(
            value => value.Equals("gzip", StringComparison.OrdinalIgnoreCase)) ||
        response.RequestMessage?.RequestUri?.AbsolutePath.EndsWith(
            ".gz",
            StringComparison.OrdinalIgnoreCase) == true;

    private static (string Unit, string Interval) MapTimeframe(string timeframe) =>
        timeframe switch
        {
            "1m" => ("minutes", "1"),
            "5m" => ("minutes", "5"),
            "15m" => ("minutes", "15"),
            "1h" => ("hours", "1"),
            "1d" => ("days", "1"),
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe)),
        };

    private static (string InstrumentType, string MarketSegment)? MapInstrumentType(
        string providerType) => providerType.ToUpperInvariant() switch
        {
            "INDEX" => ("INDEX", "INDEX"),
            "FUT" => ("FUTURE", "FUTURES"),
            "CE" or "PE" => ("OPTION", "OPTIONS"),
            "ETF" => ("ETF", "CASH"),
            "EQ" or "BE" or "BL" or "SM" or "ST" => ("EQUITY", "CASH"),
            _ => null,
        };

    private static string? MapExchangeCode(
        string? exchange,
        string segment)
    {
        if (!string.IsNullOrWhiteSpace(exchange))
        {
            return exchange.ToUpperInvariant();
        }

        var separator = segment.IndexOf('_');
        return separator > 0
            ? segment[..separator].ToUpperInvariant()
            : null;
    }

    private static void ValidateHistoricalRequest(HistoricalCandleRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProviderInstrumentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);

        if (!MarketDataContractV1.Timeframes.Contains(request.Timeframe))
        {
            throw new ArgumentOutOfRangeException(nameof(request.Timeframe));
        }

        if (request.ToDate < request.FromDate)
        {
            throw new ArgumentException("ToDate must be on or after FromDate.");
        }
    }

    private static string BuildCandleEventId(
        string instrumentKey,
        string timeframe,
        DateTimeOffset openAtUtc) =>
        $"{instrumentKey}|{timeframe}|{openAtUtc:O}";

    private static bool TryReadTimestamp(
        JsonElement element,
        out DateTimeOffset value)
    {
        if (element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out value))
        {
            value = value.ToUniversalTime();
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String &&
            decimal.TryParse(
                element.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static decimal? ReadOptionalDecimal(JsonElement item, string propertyName) =>
        TryGetProperty(item, propertyName, out var value) &&
        TryReadDecimal(value, out var parsed)
            ? parsed
            : null;

    private static DateOnly? ReadOptionalDate(JsonElement item, string propertyName)
    {
        if (!TryGetProperty(item, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
        {
            return DateOnly.FromDateTime(
                DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime);
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateOnly.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var date))
        {
            return date;
        }

        return null;
    }

    private static string? GetFirstString(
        JsonElement item,
        params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryGetString(item, propertyName, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool TryGetString(
        JsonElement item,
        string propertyName,
        out string value)
    {
        if (TryGetProperty(item, propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetProperty(
        JsonElement item,
        string propertyName,
        out JsonElement value)
    {
        if (item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static void AddMetadata(
        JsonElement item,
        IDictionary<string, string> metadata,
        string propertyName)
    {
        if (!TryGetProperty(item, propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        metadata[propertyName] = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
    }
}
