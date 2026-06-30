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
    }

    private static MarketDataPublicationFactory CreateFactory(bool enabled) =>
        new(new MarketDataPublicationOptions
        {
            Enabled = enabled,
            Environment = "PAPER",
            Producer = "ThesisPulse.MarketData.Service",
            ProducerVersion = "test",
            ConfigurationVersion = "test-v1",
        });

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
