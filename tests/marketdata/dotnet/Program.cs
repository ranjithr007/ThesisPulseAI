using ThesisPulse.Infrastructure.Brokers.Upstox;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.MarketData;

var failures = new List<string>();
var freshnessOptions = new MarketDataFreshnessOptions();
freshnessOptions.Validate();
var evaluator = new MarketDataFreshnessEvaluator(freshnessOptions);

Run("recent closed candle is valid", () =>
{
    var now = DateTimeOffset.UtcNow;
    var candle = CreateCandle(now.AddMinutes(-1), now, "recent");
    var result = evaluator.EvaluateCandle(candle, now);

    AssertEqual(MarketDataQualityStatusV1.Valid, result.QualityStatus);
    AssertTrue(result.IsUsableForNewExposure, "Recent closed candle must be usable.");
});

Run("old candle is stale", () =>
{
    var now = DateTimeOffset.UtcNow;
    var candle = CreateCandle(now.AddMinutes(-20), now.AddMinutes(-19), "stale");
    var result = evaluator.EvaluateCandle(candle, now);

    AssertEqual(MarketDataQualityStatusV1.Stale, result.QualityStatus);
    AssertFalse(result.IsUsableForNewExposure, "Stale candle must block new exposure.");
});

Run("invalid OHLC is rejected", () =>
{
    var now = DateTimeOffset.UtcNow;
    var candle = CreateCandle(now.AddMinutes(-1), now, "invalid") with
    {
        HighPrice = 99m,
        LowPrice = 101m,
    };
    var result = evaluator.EvaluateCandle(candle, now);

    AssertEqual(MarketDataQualityStatusV1.Invalid, result.QualityStatus);
    AssertFalse(result.IsUsableForExit, "Invalid candle must not be usable for exits.");
});

Run("decoded Upstox feed is normalized", () =>
{
    var normalizer = new UpstoxLiveFeedNormalizer();
    var eventTime = DateTimeOffset.UtcNow.AddSeconds(-1);
    var receivedAt = DateTimeOffset.UtcNow;
    var feeds = new[]
    {
        new UpstoxDecodedMarketFeed(
            InstrumentKey: "NSE_INDEX|Nifty 50",
            FeedType: "full",
            ExchangeTimestampMilliseconds: eventTime.ToUnixTimeMilliseconds(),
            LastTradedPrice: 25000m,
            LastTradedQuantity: 1m,
            LastTradeTimestampMilliseconds: eventTime.ToUnixTimeMilliseconds(),
            PreviousClosePrice: 24950m,
            OpenInterest: null,
            TotalBuyQuantity: 100m,
            TotalSellQuantity: 80m,
            Candles: new[]
            {
                new UpstoxDecodedCandle(
                    "I1",
                    eventTime.AddMinutes(-1).ToString("O"),
                    24990m,
                    25010m,
                    24980m,
                    25000m,
                    1000m),
            },
            SourceSequence: "sequence-1",
            RawPayloadJson: "{\"type\":\"full\"}"),
    };

    var updates = normalizer.Normalize(feeds, receivedAt);
    var update = updates.Single();

    AssertEqual("UPSTOX", update.ProviderCode);
    AssertEqual("NSE_INDEX|Nifty 50", update.ProviderInstrumentKey);
    AssertEqual("1m", update.CandleSnapshots.Single().Timeframe);
    AssertEqual("NSE_INDEX|Nifty 50|sequence-1", update.SourceEventId);
});

RunAsync("historical candle storage is idempotent", async () =>
{
    var store = new InMemoryMarketDataStore(evaluator);
    var now = DateTimeOffset.UtcNow;
    var candle = CreateCandle(now.AddMinutes(-1), now, "duplicate");
    var request = new HistoricalCandleRequestV1(
        candle.ProviderInstrumentKey,
        candle.Timeframe,
        DateOnly.FromDateTime(now.UtcDateTime),
        DateOnly.FromDateTime(now.UtcDateTime),
        Guid.NewGuid().ToString("D"));

    var first = await store.PersistHistoricalCandlesAsync(request, new[] { candle });
    var second = await store.PersistHistoricalCandlesAsync(request, new[] { candle });

    AssertEqual(1, first.Accepted);
    AssertEqual(0, first.Duplicates);
    AssertEqual(0, second.Accepted);
    AssertEqual(1, second.Duplicates);
});

RunAsync("instrument sync remains non-tradeable", async () =>
{
    var store = new InMemoryMarketDataStore(evaluator);
    var instrument = new CanonicalInstrumentV1(
        "UPSTOX",
        "NSE_INDEX|Nifty 50",
        "NSE",
        "NSE_INDEX",
        "NIFTY50",
        "Nifty 50",
        "INDEX",
        "INDEX",
        Isin: null,
        UnderlyingProviderInstrumentKey: null,
        ExpiryDate: null,
        StrikePrice: null,
        OptionType: null,
        TickSize: 0.05m,
        LotSize: 1m,
        FreezeQuantity: null,
        IsTradeAllowed: false,
        IsShortAllowed: false,
        EffectiveFromDate: DateOnly.FromDateTime(DateTime.UtcNow),
        Metadata: null);

    var result = await store.SynchronizeAsync(
        new[] { instrument },
        DateTimeOffset.UtcNow);

    AssertEqual(1, result.Created);
    AssertFalse(instrument.IsTradeAllowed, "Synchronization must not grant trading permission.");
    AssertFalse(instrument.IsShortAllowed, "Synchronization must not grant short permission.");
});

LiveFeedTestSuite.Run(failures);
DerivativesMarketDataTestSuite.Run(failures);

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} Market Data test(s) failed.");
    return 1;
}

Console.WriteLine("All Market Data foundation tests passed.");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

void RunAsync(string name, Func<Task> test)
{
    try
    {
        test().GetAwaiter().GetResult();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

static CanonicalCandleV1 CreateCandle(
    DateTimeOffset openAtUtc,
    DateTimeOffset closeAtUtc,
    string eventSuffix) =>
    new(
        ProviderCode: "UPSTOX",
        ProviderInstrumentKey: "NSE_INDEX|Nifty 50",
        SourceEventId: $"NSE_INDEX|Nifty 50|1m|{eventSuffix}",
        Timeframe: "1m",
        OpenAtUtc: openAtUtc,
        CloseAtUtc: closeAtUtc,
        OpenPrice: 100m,
        HighPrice: 102m,
        LowPrice: 99m,
        ClosePrice: 101m,
        VolumeQuantity: 1000m,
        OpenInterest: null,
        TradeCount: null,
        VwapPrice: null,
        IsClosed: true,
        PublishedAtUtc: closeAtUtc,
        ReceivedAtUtc: closeAtUtc,
        SourceVersion: "test",
        RawPayloadJson: "[1,100,102,99,101,1000]");

static void AssertTrue(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertFalse(bool value, string message) => AssertTrue(!value, message);

static void AssertEqual<T>(T expected, T actual)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"Expected '{expected}' but received '{actual}'.");
    }
}
