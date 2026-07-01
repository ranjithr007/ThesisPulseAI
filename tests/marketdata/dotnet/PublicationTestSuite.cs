using System.Text.Json;
using ThesisPulse.Shared.Contracts.MarketData.V1;
using ThesisPulse.Shared.Infrastructure.MarketData;
using ThesisPulse.Shared.Infrastructure.Messaging;

internal static class PublicationTestSuite
{
    public static void Run(ICollection<string> failures)
    {
        Execute(failures, "quote publication identity is deterministic", () =>
        {
            var factory = CreateFactory(enabled: true);
            var now = DateTimeOffset.UtcNow;
            var update = new CanonicalLiveMarketUpdateV1(
                "UPSTOX", "NSE_INDEX|Nifty 50", "event-1",
                now, now, now, 25000m, 1m, 24900m, null, 100m, 90m,
                Array.Empty<CanonicalLiveCandleV1>(), "test", "{}");
            var assessment = Assessment(now);
            var first = factory.CreateQuote(update, assessment, "correlation-1");
            var second = factory.CreateQuote(update, assessment, "correlation-1");

            AssertTrue(first is not null, "Quote publication was not created.");
            AssertEqual(first!.Metadata.MessageId, second!.Metadata.MessageId);
            AssertEqual(
                MarketDataPublicationContractV1.QuoteEventType,
                first.Metadata.EventType);
            AssertEqual(
                MarketDataPublicationContractV1.ContractVersion,
                first.Metadata.ContractVersion);
            var payload = JsonSerializer.Deserialize<MarketQuotePublishedV1>(
                first.PayloadJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            AssertEqual("NSE_INDEX|Nifty 50", payload!.InstrumentKey);
        });

        Execute(failures, "disabled publication creates no event", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var factory = CreateFactory(enabled: false);
            var update = new CanonicalLiveMarketUpdateV1(
                "UPSTOX", "NSE_INDEX|Nifty 50", "event-2",
                now, now, now, 25000m, null, null, null, null, null,
                Array.Empty<CanonicalLiveCandleV1>(), "test", "{}");
            AssertTrue(
                factory.CreateQuote(update, Assessment(now), "correlation-2") is null,
                "Disabled publication must remain fail closed.");
        });

        Execute(failures, "option-chain publication identity is deterministic", () =>
        {
            var now = new DateTimeOffset(2026, 7, 1, 3, 45, 0, TimeSpan.Zero);
            var expiry = new DateOnly(2026, 7, 30);
            var source = OptionChainSource(now, expiry, "option-event-1", revision: 0);
            var entries = new[]
            {
                new MarketOptionChainEntryPublishedV1(
                    Guid.Parse("11111111-1111-5111-8111-111111111111"),
                    "NSE_FO|NIFTY-20260730-25000-CE",
                    expiry,
                    25000m,
                    "CALL",
                    100m,
                    25m,
                    500m,
                    0.20m,
                    0.50m,
                    75m,
                    MarketDataQualityStatusV1.Valid,
                    "provider-greeks-v1"),
            };
            var factory = CreateFactory(enabled: true, optionChainEnabled: true);
            var snapshotUid = Guid.Parse("22222222-2222-5222-8222-222222222222");

            var first = factory.CreateOptionChain(
                source,
                snapshotUid,
                "COMPLETE",
                MarketDataQualityStatusV1.Valid,
                true,
                entries);
            var second = factory.CreateOptionChain(
                source,
                snapshotUid,
                "COMPLETE",
                MarketDataQualityStatusV1.Valid,
                true,
                entries);

            AssertTrue(first is not null, "Option-chain publication was not created.");
            AssertEqual(first!.Metadata.MessageId, second!.Metadata.MessageId);
            AssertEqual(
                MarketDataPublicationContractV1.OptionChainEventType,
                first.Metadata.EventType);
            AssertTrue(
                MarketDataPublicationContractV1.EventTypes.Contains(
                    MarketDataPublicationContractV1.OptionChainEventType),
                "Option-chain event is missing from publication filtering.");
            var payload = JsonSerializer.Deserialize<MarketOptionChainPublishedV1>(
                first.PayloadJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            AssertEqual(snapshotUid, payload!.SnapshotUid);
            AssertEqual("NSE_INDEX|Nifty 50", payload.UnderlyingInstrumentKey);
            AssertEqual(75m, payload.Entries.Single().ContractMultiplier);
        });

        Execute(failures, "ineligible option chains are not published", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var expiry = DateOnly.FromDateTime(now.UtcDateTime.AddDays(20));
            var source = OptionChainSource(now, expiry, "option-event-2", revision: 0);
            var entries = new[]
            {
                new MarketOptionChainEntryPublishedV1(
                    Guid.NewGuid(),
                    "NSE_FO|OPTION",
                    expiry,
                    25000m,
                    "PUT",
                    90m,
                    20m,
                    400m,
                    0.21m,
                    -0.50m,
                    75m,
                    MarketDataQualityStatusV1.Valid,
                    "provider-greeks-v1"),
            };
            var factory = CreateFactory(enabled: true, optionChainEnabled: true);

            AssertTrue(
                factory.CreateOptionChain(
                    source,
                    Guid.NewGuid(),
                    "PARTIAL",
                    MarketDataQualityStatusV1.Degraded,
                    false,
                    entries) is null,
                "Partial option chains must not enter intelligence publication.");
        });

        Execute(failures, "consumer checkpoint only advances", () =>
        {
            var store = new InMemoryMarketDataConsumerCheckpointStore();
            var now = DateTimeOffset.UtcNow;
            store.AdvanceAsync(new(
                "consumer", "market-data.v1", "*", 10, Guid.NewGuid(), now))
                .GetAwaiter().GetResult();
            store.AdvanceAsync(new(
                "consumer", "market-data.v1", "*", 5, Guid.NewGuid(), now.AddSeconds(-1)))
                .GetAwaiter().GetResult();
            var value = store.GetAsync("consumer", "market-data.v1", "*")
                .GetAwaiter().GetResult();
            AssertEqual(10L, value!.LastPosition);
        });

        Execute(failures, "replay filters non-market events and uses cursor", () =>
        {
            var target = new InMemoryDispatchTarget();
            var now = DateTimeOffset.UtcNow;
            target.SendAsync(Message(
                Guid.NewGuid(),
                MarketDataPublicationContractV1.QuoteEventType,
                now)).GetAwaiter().GetResult();
            target.SendAsync(Message(
                Guid.NewGuid(),
                "signal.status.changed.v1",
                now.AddSeconds(1))).GetAwaiter().GetResult();
            target.SendAsync(Message(
                Guid.NewGuid(),
                MarketDataPublicationContractV1.CandleEventType,
                now.AddSeconds(2))).GetAwaiter().GetResult();
            var replay = new InMemoryMarketDataReplayStore(target);
            var firstPage = replay.LoadAsync(0, 10).GetAwaiter().GetResult();
            var secondPage = replay.LoadAsync(1, 10).GetAwaiter().GetResult();

            AssertEqual(2, firstPage.Count);
            AssertEqual(1, secondPage.Count);
            AssertEqual(2L, secondPage.Single().StreamPosition);
        });

        Execute(failures, "publication migrations are fail closed", () =>
        {
            var root = Directory.GetCurrentDirectory();
            var migration = File.ReadAllText(Path.Combine(
                root,
                "database",
                "migrations",
                "V0011__create_market_data_publication_state.sql"));
            var safety = File.ReadAllText(Path.Combine(
                root,
                "database",
                "migrations",
                "V0012__disable_market_data_candle_publication_by_default.sql"));
            AssertTrue(migration.Contains("consumer_checkpoints"), "Checkpoint DDL is missing.");
            AssertTrue(migration.Contains("tr_candles_publish_v1"), "Candle trigger is missing.");
            AssertTrue(safety.Contains("DISABLE TRIGGER"), "Candle trigger must default disabled.");
        });

        Execute(failures, "option-chain SQL publication shares snapshot transaction", () =>
        {
            var root = Directory.GetCurrentDirectory();
            var source = File.ReadAllText(Path.Combine(
                root,
                "src",
                "ThesisPulse.Shared.Infrastructure",
                "MarketData",
                "SqlServerDerivativesMarketDataStore.OptionChainWrite.cs"));
            var outbox = File.ReadAllText(Path.Combine(
                root,
                "src",
                "ThesisPulse.Shared.Infrastructure",
                "MarketData",
                "SqlServerDerivativesMarketDataStore.Publication.cs"));
            AssertTrue(
                source.Contains("EnqueueOptionChainPublicationAsync"),
                "Option-chain persistence does not enqueue its publication.");
            AssertTrue(
                source.IndexOf("EnqueueOptionChainPublicationAsync", StringComparison.Ordinal) <
                source.IndexOf("CommitAsync", StringComparison.Ordinal),
                "Option-chain outbox insertion must occur before transaction commit.");
            AssertTrue(
                outbox.Contains("MARKET_DATA_FANOUT"),
                "Option-chain outbox destination is incorrect.");
        });
    }

    private static MarketDataPublicationFactory CreateFactory(
        bool enabled,
        bool optionChainEnabled = false) =>
        new(new MarketDataPublicationOptions
        {
            Enabled = enabled,
            OptionChainEnabled = optionChainEnabled,
            Environment = "PAPER",
            Producer = "ThesisPulse.MarketData.Service",
            ProducerVersion = "test",
            ConfigurationVersion = "test-v1",
        });

    private static CanonicalOptionChainSnapshotV1 OptionChainSource(
        DateTimeOffset now,
        DateOnly expiry,
        string sourceEventId,
        int revision) =>
        new(
            "UPSTOX",
            sourceEventId,
            revision,
            "NSE_INDEX|Nifty 50",
            expiry,
            now,
            now,
            now.AddSeconds(1),
            25000m,
            "provider-option-chain-v1",
            "provider-greeks-v1",
            Guid.Parse("33333333-3333-4333-8333-333333333333").ToString("D"),
            Array.Empty<CanonicalOptionChainEntryV1>(),
            "{}");

    private static MarketDataFreshnessAssessmentV1 Assessment(DateTimeOffset now) =>
        new(
            MarketDataQualityStatusV1.Valid,
            now,
            now,
            0,
            5000,
            true,
            true,
            "test-policy",
            Array.Empty<string>());

    private static OutboxMessage Message(
        Guid messageId,
        string eventType,
        DateTimeOffset occurredAt) =>
        new(
            new(
                messageId,
                eventType,
                "1.0",
                occurredAt,
                Guid.NewGuid().ToString("D"),
                null,
                "test",
                "test",
                "PAPER",
                "test"),
            "{}",
            OutboxMessageStatus.Published,
            1,
            occurredAt,
            null);

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
