using ThesisPulse.Infrastructure.Brokers.Upstox;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.MarketData;

internal static class RecoveryTestSuite
{
    public static void Run(
        ICollection<string> failures,
        IMarketDataFreshnessEvaluator freshnessEvaluator)
    {
        Execute(failures, "subscription plan is deterministic and prioritized", () =>
        {
            var options = new ConfiguredMarketDataSubscriptionOptions
            {
                ProviderCode = "UPSTOX",
                FeedMode = "full",
                RecoveryTimeframe = "5m",
                InstrumentKeys = new[]
                {
                    "NSE_INDEX|Nifty Bank",
                    "NSE_INDEX|Nifty 50",
                },
            };
            var catalog = new ConfiguredMarketDataSubscriptionCatalog(
                options,
                TimeProvider.System);
            var first = catalog.GetPlanAsync("UPSTOX", "full")
                .GetAwaiter().GetResult();
            var second = catalog.GetPlanAsync("UPSTOX", "full")
                .GetAwaiter().GetResult();

            AssertEqual(first.Version, second.Version);
            AssertEqual(2, first.Items.Count);
            AssertEqual(1, first.Items.OrderBy(item => item.Priority).First().Priority);
        });

        Execute(failures, "session-aware detector finds missing closed candles", () =>
        {
            var options = new MarketDataRecoveryOptions
            {
                GracePeriodSeconds = 0,
                SessionOpenIndia = new TimeOnly(9, 15),
                SessionCloseIndia = new TimeOnly(15, 30),
            };
            var detector = new MarketDataGapDetector(options);
            var evaluatedAtUtc = new DateTimeOffset(
                2026, 6, 29, 4, 32, 0, TimeSpan.Zero);
            var latest = new StoredCandleV1(
                1,
                Guid.NewGuid(),
                "NSE_INDEX|Nifty 50",
                "5m",
                new DateTimeOffset(2026, 6, 29, 4, 10, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 29, 4, 15, 0, TimeSpan.Zero),
                100m,
                102m,
                99m,
                101m,
                1000m,
                MarketDataQualityStatusV1.Valid,
                true,
                new DateTimeOffset(2026, 6, 29, 4, 15, 1, TimeSpan.Zero));
            var subscription = new MarketDataSubscriptionItem(
                "NSE_INDEX|Nifty 50",
                "full",
                "5m",
                1);

            var gap = detector.Detect(
                subscription,
                new[] { latest },
                evaluatedAtUtc);

            AssertTrue(gap is not null, "Expected a missing-candle gap.");
            AssertEqual("MISSING_CLOSED_CANDLES", gap!.ReasonCode);
            AssertTrue(gap.ExpectedRecordCount >= 3, "Expected at least three missing bars.");
        });

        Execute(failures, "weekend reconciliation does not create a false gap", () =>
        {
            var detector = new MarketDataGapDetector(new MarketDataRecoveryOptions());
            var subscription = new MarketDataSubscriptionItem(
                "NSE_INDEX|Nifty 50",
                "full",
                "5m",
                1);
            var saturdayUtc = new DateTimeOffset(
                2026, 6, 27, 5, 0, 0, TimeSpan.Zero);

            var gap = detector.Detect(
                subscription,
                Array.Empty<StoredCandleV1>(),
                saturdayUtc);

            AssertTrue(gap is null, "Weekend must not produce an intraday gap.");
        });

        ExecuteAsync(failures, "live provisional candle is revised by final candle", async () =>
        {
            var store = new InMemoryMarketDataStore(freshnessEvaluator);
            var openAt = DateTimeOffset.UtcNow.AddMinutes(-6);
            var live = new CanonicalLiveMarketUpdateV1(
                "UPSTOX",
                "NSE_INDEX|Nifty 50",
                "live-1",
                openAt.AddMinutes(3),
                openAt.AddMinutes(3),
                openAt.AddMinutes(3),
                101m,
                1m,
                100m,
                null,
                null,
                null,
                new[]
                {
                    new CanonicalLiveCandleV1(
                        "5m", openAt, 100m, 102m, 99m, 101m, 500m),
                },
                "test-live",
                "{\"source\":\"live\"}");
            await store.PersistLiveUpdatesAsync(
                new[] { live },
                Guid.NewGuid().ToString("D"));

            var finalCandle = new CanonicalCandleV1(
                "UPSTOX",
                "NSE_INDEX|Nifty 50",
                "final-1",
                "5m",
                openAt,
                openAt.AddMinutes(5),
                100m,
                103m,
                99m,
                102m,
                700m,
                null,
                null,
                null,
                true,
                openAt.AddMinutes(5),
                openAt.AddMinutes(6),
                "test-rest",
                "[1,100,103,99,102,700]");
            var tradeDate = DateOnly.FromDateTime(openAt.UtcDateTime);
            var request = new HistoricalCandleRequestV1(
                finalCandle.ProviderInstrumentKey,
                "5m",
                tradeDate,
                tradeDate,
                Guid.NewGuid().ToString("D"));
            await store.PersistHistoricalCandlesAsync(request, new[] { finalCandle });

            var candles = await store.GetLatestCandlesAsync(
                finalCandle.ProviderInstrumentKey,
                "5m",
                10);
            var current = candles.Single();
            AssertEqual(102m, current.ClosePrice);
            AssertEqual(700m, current.VolumeQuantity);
            AssertTrue(current.IsUsableForNewExposure, "Final candle must be usable.");
        });

        Execute(failures, "SQL subscription mode permits empty configured keys", () =>
        {
            var options = new UpstoxLiveFeedOptions
            {
                Enabled = true,
                Mode = UpstoxLiveFeedModes.Full,
                InstrumentKeys = Array.Empty<string>(),
            };

            options.Validate(
                providerEnabled: true,
                requireConfiguredInstrumentKeys: false);
        });
    }

    private static void Execute(
        ICollection<string> failures,
        string name,
        Action test)
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

    private static void ExecuteAsync(
        ICollection<string> failures,
        string name,
        Func<Task> test)
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

    private static void AssertTrue(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Expected '{expected}' but received '{actual}'.");
        }
    }
}
