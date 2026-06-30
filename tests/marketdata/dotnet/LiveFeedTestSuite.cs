using System.Text.Json;
using Google.Protobuf;
using ThesisPulse.Infrastructure.Brokers.Upstox;
using ThesisPulse.Infrastructure.Brokers.Upstox.Protobuf;
using Protobuf = ThesisPulse.Infrastructure.Brokers.Upstox.Protobuf;

internal static class LiveFeedTestSuite
{
    public static void Run(ICollection<string> failures)
    {
        Execute(failures, "subscription command uses binary V3 structure", () =>
        {
            var builder = new UpstoxSubscriptionCommandBuilder();
            var command = builder.BuildSubscribe(new UpstoxSubscriptionSnapshot(
                UpstoxLiveFeedModes.Full,
                new[] { "NSE_INDEX|Nifty 50", "NSE_INDEX|Nifty Bank" },
                "test-version"));
            using var document = JsonDocument.Parse(command.Payload);
            var root = document.RootElement;

            AssertEqual("sub", root.GetProperty("method").GetString());
            AssertEqual(
                "full",
                root.GetProperty("data").GetProperty("mode").GetString());
            AssertEqual(
                2,
                root.GetProperty("data")
                    .GetProperty("instrumentKeys")
                    .GetArrayLength());
            AssertTrue(command.Payload.Length > 0, "Subscription payload must not be empty.");
        });

        Execute(failures, "official V3 protobuf decodes to canonical input", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var marketFeed = new MarketFullFeed
            {
                Ltpc = new LTPC
                {
                    Ltp = 25000.25,
                    Ltt = now.AddMilliseconds(-100).ToUnixTimeMilliseconds(),
                    Ltq = 10,
                    Cp = 24900.50,
                },
                MarketOHLC = new MarketOHLC(),
                Oi = 5000,
                Tbq = 10000,
                Tsq = 9000,
            };
            marketFeed.MarketOHLC.Ohlc.Add(new OHLC
            {
                Interval = "I1",
                Open = 24995,
                High = 25005,
                Low = 24990,
                Close = 25000.25,
                Vol = 1000,
                Ts = now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            });

            var response = new FeedResponse
            {
                Type = Protobuf.Type.LiveFeed,
                CurrentTs = now.ToUnixTimeMilliseconds(),
                MarketInfo = new MarketInfo(),
            };
            response.MarketInfo.SegmentStatus.Add(
                "NSE_INDEX",
                MarketStatus.NormalOpen);
            response.Feeds.Add(
                "NSE_INDEX|Nifty 50",
                new Feed
                {
                    RequestMode = RequestMode.FullD5,
                    FullFeed = new FullFeed
                    {
                        MarketFF = marketFeed,
                    },
                });

            var decoder = new UpstoxMarketDataFeedDecoder();
            var envelope = decoder.Decode(response.ToByteArray());
            var feed = envelope.Feeds.Single();

            AssertEqual("live_feed", envelope.MessageType);
            AssertEqual("NORMAL_OPEN", envelope.SegmentStatuses["NSE_INDEX"]);
            AssertEqual("NSE_INDEX|Nifty 50", feed.InstrumentKey);
            AssertEqual("full", feed.FeedType);
            AssertEqual(25000.25m, feed.LastTradedPrice);
            AssertEqual(5000m, feed.OpenInterest);
            AssertEqual("I1", feed.Candles!.Single().Interval);
        });

        Execute(failures, "full D30 subscription limit is enforced", () =>
        {
            var options = new UpstoxLiveFeedOptions
            {
                Enabled = true,
                Mode = UpstoxLiveFeedModes.FullD30,
                InstrumentKeys = Enumerable.Range(1, 51)
                    .Select(index => $"NSE_FO|{index}")
                    .ToArray(),
            };

            AssertThrows<InvalidOperationException>(() =>
                options.Validate(providerEnabled: true));
        });

        Execute(failures, "health state records synchronization and persistence", () =>
        {
            var options = new UpstoxLiveFeedOptions
            {
                Enabled = true,
                Mode = UpstoxLiveFeedModes.Full,
                InstrumentKeys = new[] { "NSE_INDEX|Nifty 50" },
            };
            var health = new UpstoxLiveFeedHealthState(options);
            var now = DateTimeOffset.UtcNow;

            health.Starting(now);
            health.Authorizing(1);
            health.Connecting();
            health.Connected(now.AddSeconds(1));
            health.Subscribed();
            health.MessageReceived(
                now.AddSeconds(2),
                "market_info",
                new Dictionary<string, string>
                {
                    ["NSE_INDEX"] = "NORMAL_OPEN",
                });
            health.MessageReceived(
                now.AddSeconds(3),
                "live_feed",
                new Dictionary<string, string>());
            health.Persisted(now.AddSeconds(3), 1);

            var snapshot = health.GetSnapshot();
            AssertEqual("STREAMING", snapshot.Status);
            AssertTrue(snapshot.Connected, "Feed must remain connected.");
            AssertTrue(snapshot.HasOpenMarketSegment, "Open market status was lost.");
            AssertEqual(2L, snapshot.MessagesReceived);
            AssertEqual(1L, snapshot.UpdatesPersisted);
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

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected exception '{typeof(TException).Name}'.");
    }
}
