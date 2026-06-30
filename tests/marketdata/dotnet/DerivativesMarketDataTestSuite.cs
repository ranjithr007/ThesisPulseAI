using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.MarketData;

internal static class DerivativesMarketDataTestSuite
{
    public static void Run(List<string> failures)
    {
        RunAsync(failures, "derivative contracts and expiries synchronize", async () =>
        {
            var store = new InMemoryDerivativesMarketDataStore();
            var fixture = CreateFixture();
            var result = await store.SynchronizeContractsAsync(
                fixture.Instruments,
                fixture.Now);

            AssertEqual(3, result.ReceivedDerivatives);
            AssertEqual(3, result.Created);
            AssertEqual(0, result.Skipped);

            var contracts = await store.GetContractsAsync(
                fixture.UnderlyingKey,
                fixture.ExpiryDate,
                contractClass: null);
            AssertEqual(3, contracts.Count);
            AssertTrue(
                contracts.All(item => !item.SelectionEligible),
                "Catalog synchronization must not grant contract selection authority.");
            AssertTrue(
                contracts.Any(item => item.ContractClass == "INDEX_FUTURE"),
                "Index future contract was not classified.");
            AssertTrue(
                contracts.Count(item => item.ContractClass == "INDEX_OPTION") == 2,
                "Index option contracts were not classified.");

            var expiries = await store.GetExpiriesAsync(
                fixture.UnderlyingKey,
                marketSegment: null);
            AssertEqual(2, expiries.Count);
            AssertTrue(
                expiries.All(item => item.ExpiryType == "MONTHLY"),
                "Expiry type must retain canonical metadata.");
        });

        RunAsync(failures, "futures basis is deterministic and idempotent", async () =>
        {
            var store = new InMemoryDerivativesMarketDataStore();
            var fixture = CreateFixture();
            await store.SynchronizeContractsAsync(fixture.Instruments, fixture.Now);
            var observation = CreateBasis(fixture, "basis-1", fixture.Now, 25000m, 25050m);

            var first = await store.PersistFuturesBasisAsync(observation);
            var duplicate = await store.PersistFuturesBasisAsync(observation);

            AssertEqual("CREATED", first.Outcome);
            AssertEqual("DUPLICATE", duplicate.Outcome);
            AssertNotNull(first.Observation, "Basis observation was not created.");
            AssertEqual(50m, first.Observation!.BasisAmount);
            AssertEqual(0.002m, first.Observation.BasisFraction);
            AssertTrue(
                first.Observation.DaysToExpiry > 0,
                "Days to expiry must be calculated from the event date.");
            AssertTrue(
                first.Observation.AnnualizedBasisFraction is not null,
                "Non-expiry-day basis must be annualized.");
        });

        RunAsync(failures, "basis point-in-time reads respect cutoff", async () =>
        {
            var store = new InMemoryDerivativesMarketDataStore();
            var fixture = CreateFixture();
            await store.SynchronizeContractsAsync(fixture.Instruments, fixture.Now);
            var firstAt = fixture.Now.AddMinutes(1);
            var secondAt = fixture.Now.AddMinutes(2);
            await store.PersistFuturesBasisAsync(
                CreateBasis(fixture, "basis-old", firstAt, 25000m, 25020m));
            await store.PersistFuturesBasisAsync(
                CreateBasis(fixture, "basis-new", secondAt, 25000m, 25060m));

            var historical = await store.GetLatestFuturesBasisAsync(
                fixture.FutureKey,
                firstAt.AddSeconds(1));
            var latest = await store.GetLatestFuturesBasisAsync(
                fixture.FutureKey,
                asOfUtc: null);

            AssertNotNull(historical, "Historical basis observation was not found.");
            AssertNotNull(latest, "Latest basis observation was not found.");
            AssertEqual(20m, historical!.BasisAmount);
            AssertEqual(60m, latest!.BasisAmount);
        });

        RunAsync(failures, "complete option chain is normalized and idempotent", async () =>
        {
            var store = new InMemoryDerivativesMarketDataStore();
            var fixture = CreateFixture();
            await store.SynchronizeContractsAsync(fixture.Instruments, fixture.Now);
            var snapshot = CreateOptionChain(fixture, "chain-1", includeInvalid: false);

            var first = await store.PersistOptionChainAsync(snapshot);
            var duplicate = await store.PersistOptionChainAsync(snapshot);

            AssertEqual("CREATED", first.Outcome);
            AssertEqual("DUPLICATE", duplicate.Outcome);
            AssertNotNull(first.Snapshot, "Option-chain snapshot was not created.");
            AssertEqual("COMPLETE", first.Snapshot!.SnapshotStatus);
            AssertTrue(
                first.Snapshot.IsPointInTimeEligible,
                "Complete canonical chain must be point-in-time eligible.");
            AssertEqual(2, first.AcceptedContracts);
            AssertEqual(0, first.RejectedContracts);
            AssertTrue(
                first.Snapshot.Entries.All(item => item.OpenInterestChange == 50m),
                "Open-interest changes were not normalized.");
        });

        RunAsync(failures, "partial option chain fails closed", async () =>
        {
            var store = new InMemoryDerivativesMarketDataStore();
            var fixture = CreateFixture();
            await store.SynchronizeContractsAsync(fixture.Instruments, fixture.Now);

            var result = await store.PersistOptionChainAsync(
                CreateOptionChain(fixture, "chain-partial", includeInvalid: true));

            AssertNotNull(result.Snapshot, "Partial option-chain snapshot was not stored.");
            AssertEqual("PARTIAL", result.Snapshot!.SnapshotStatus);
            AssertFalse(
                result.Snapshot.IsPointInTimeEligible,
                "Partial option-chain snapshots must not enter intelligence workflows.");
            AssertEqual(2, result.AcceptedContracts);
            AssertEqual(1, result.RejectedContracts);
            AssertTrue(
                result.Warnings.Any(item => item.Contains("ask price", StringComparison.Ordinal)),
                "Rejected leg reason was not retained.");
        });
    }

    private static Fixture CreateFixture()
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveDate = DateOnly.FromDateTime(now.UtcDateTime);
        var expiryDate = effectiveDate.AddDays(30);
        const string underlyingKey = "NSE_INDEX|Nifty 50";
        const string futureKey = "NSE_FO|NIFTY-FUT";
        const string callKey = "NSE_FO|NIFTY-25000-CE";
        const string putKey = "NSE_FO|NIFTY-25000-PE";
        var derivativeMetadata = new Dictionary<string, string>
        {
            ["underlyingType"] = "INDEX",
            ["expiryType"] = "MONTHLY",
            ["settlementType"] = "CASH",
            ["contractMultiplier"] = "75",
        };
        var instruments = new[]
        {
            Instrument(
                underlyingKey,
                "NIFTY50",
                "INDEX",
                "INDEX",
                underlyingKey: null,
                expiryDate: null,
                strike: null,
                optionType: null,
                lotSize: 1m,
                effectiveDate,
                metadata: null),
            Instrument(
                futureKey,
                "NIFTY-FUT",
                "FUTURE",
                "FUTURES",
                underlyingKey,
                expiryDate,
                strike: null,
                optionType: null,
                lotSize: 75m,
                effectiveDate,
                derivativeMetadata),
            Instrument(
                callKey,
                "NIFTY-25000-CE",
                "OPTION",
                "OPTIONS",
                underlyingKey,
                expiryDate,
                25000m,
                "CALL",
                75m,
                effectiveDate,
                derivativeMetadata),
            Instrument(
                putKey,
                "NIFTY-25000-PE",
                "OPTION",
                "OPTIONS",
                underlyingKey,
                expiryDate,
                25000m,
                "PUT",
                75m,
                effectiveDate,
                derivativeMetadata),
        };
        return new Fixture(
            now,
            expiryDate,
            underlyingKey,
            futureKey,
            callKey,
            putKey,
            instruments);
    }

    private static CanonicalInstrumentV1 Instrument(
        string key,
        string symbol,
        string instrumentType,
        string marketSegment,
        string? underlyingKey,
        DateOnly? expiryDate,
        decimal? strike,
        string? optionType,
        decimal lotSize,
        DateOnly effectiveDate,
        IReadOnlyDictionary<string, string>? metadata) =>
        new(
            "UPSTOX",
            key,
            "NSE",
            marketSegment == "INDEX" ? "NSE_INDEX" : "NSE_FO",
            symbol,
            symbol,
            instrumentType,
            marketSegment,
            Isin: null,
            underlyingKey,
            expiryDate,
            strike,
            optionType,
            TickSize: 0.05m,
            lotSize,
            FreezeQuantity: null,
            IsTradeAllowed: false,
            IsShortAllowed: false,
            effectiveDate,
            metadata);

    private static CanonicalFuturesBasisObservationV1 CreateBasis(
        Fixture fixture,
        string sourceEventId,
        DateTimeOffset eventAtUtc,
        decimal underlyingPrice,
        decimal futurePrice) =>
        new(
            "UPSTOX",
            sourceEventId,
            Revision: 0,
            fixture.UnderlyingKey,
            fixture.FutureKey,
            eventAtUtc,
            eventAtUtc,
            eventAtUtc,
            underlyingPrice,
            futurePrice,
            "test-v1",
            Guid.NewGuid().ToString("D"),
            "{}");

    private static CanonicalOptionChainSnapshotV1 CreateOptionChain(
        Fixture fixture,
        string sourceEventId,
        bool includeInvalid)
    {
        var entries = new List<CanonicalOptionChainEntryV1>
        {
            OptionEntry(fixture.CallKey, "CALL", fixture.Now, 100m, 101m),
            OptionEntry(fixture.PutKey, "PUT", fixture.Now, 90m, 91m),
        };
        if (includeInvalid)
        {
            entries.Add(OptionEntry(
                "NSE_FO|INVALID-LEG",
                "CALL",
                fixture.Now,
                bid: 110m,
                ask: 100m));
        }

        return new CanonicalOptionChainSnapshotV1(
            "UPSTOX",
            sourceEventId,
            Revision: 0,
            fixture.UnderlyingKey,
            fixture.ExpiryDate,
            fixture.Now,
            fixture.Now,
            fixture.Now,
            UnderlyingPrice: 25000m,
            SourceVersion: "test-v1",
            CalculationSourceVersion: "greeks-test-v1",
            Guid.NewGuid().ToString("D"),
            entries,
            "{}");
    }

    private static CanonicalOptionChainEntryV1 OptionEntry(
        string key,
        string optionType,
        DateTimeOffset quoteAtUtc,
        decimal bid,
        decimal ask) =>
        new(
            key,
            quoteAtUtc,
            StrikePrice: 25000m,
            optionType,
            BidPrice: bid,
            AskPrice: ask,
            LastPrice: (bid + ask) / 2m,
            BidQuantity: 75m,
            AskQuantity: 75m,
            VolumeQuantity: 1000m,
            OpenInterest: 1050m,
            PreviousOpenInterest: 1000m,
            ImpliedVolatility: 0.15m,
            Delta: optionType == "CALL" ? 0.50m : -0.50m,
            Gamma: 0.01m,
            Theta: -5m,
            Vega: 10m,
            Rho: 1m,
            GreeksSourceVersion: "greeks-test-v1",
            QualityStatus: MarketDataQualityStatusV1.Valid,
            Metadata: null);

    private static void RunAsync(
        List<string> failures,
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

    private static void AssertFalse(bool value, string message) =>
        AssertTrue(!value, message);

    private static void AssertNotNull(object? value, string message) =>
        AssertTrue(value is not null, message);

    private static void AssertEqual<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Expected '{expected}' but received '{actual}'.");
        }
    }

    private sealed record Fixture(
        DateTimeOffset Now,
        DateOnly ExpiryDate,
        string UnderlyingKey,
        string FutureKey,
        string CallKey,
        string PutKey,
        IReadOnlyCollection<CanonicalInstrumentV1> Instruments);
}
