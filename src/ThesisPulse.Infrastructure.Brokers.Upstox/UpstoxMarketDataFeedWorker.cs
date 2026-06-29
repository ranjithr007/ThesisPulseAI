using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThesisPulse.Shared.Infrastructure.MarketData;

namespace ThesisPulse.Infrastructure.Brokers.Upstox;

public sealed class UpstoxMarketDataFeedWorker(
    IMarketDataProvider marketDataProvider,
    IMarketDataStore marketDataStore,
    IUpstoxSubscriptionProvider subscriptionProvider,
    IUpstoxSubscriptionCommandBuilder commandBuilder,
    IUpstoxWebSocketConnectionFactory connectionFactory,
    IUpstoxMarketDataFeedDecoder decoder,
    IUpstoxLiveFeedNormalizer normalizer,
    UpstoxLiveFeedOptions options,
    UpstoxLiveFeedHealthState healthState,
    TimeProvider timeProvider,
    ILogger<UpstoxMarketDataFeedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            healthState.Stopped(timeProvider.GetUtcNow());
            return;
        }

        healthState.Starting(timeProvider.GetUtcNow());
        var connectionAttempt = 0;
        var reconnectCount = 0;
        var consecutiveFailures = 0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                connectionAttempt++;

                try
                {
                    await RunConnectionAsync(connectionAttempt, stoppingToken);

                    if (!stoppingToken.IsCancellationRequested)
                    {
                        throw new WebSocketException(
                            "Upstox WebSocket receive loop ended unexpectedly.");
                    }
                }
                catch (OperationCanceledException) when (
                    stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    reconnectCount++;
                    var connectionSnapshot = healthState.GetSnapshot();

                    if (connectionSnapshot.ConnectedAtUtc.HasValue &&
                        timeProvider.GetUtcNow() -
                        connectionSnapshot.ConnectedAtUtc.Value >=
                        TimeSpan.FromSeconds(options.StableConnectionResetSeconds))
                    {
                        consecutiveFailures = 0;
                    }

                    consecutiveFailures++;
                    var delay = CalculateReconnectDelay(consecutiveFailures);
                    var nextRetryAtUtc = timeProvider.GetUtcNow().Add(delay);
                    healthState.Reconnecting(
                        exception.Message,
                        nextRetryAtUtc,
                        reconnectCount);

                    logger.LogWarning(
                        exception,
                        "Upstox live feed disconnected. Reconnecting in {DelayMs} ms.",
                        delay.TotalMilliseconds);

                    try
                    {
                        await Task.Delay(delay, stoppingToken);
                    }
                    catch (OperationCanceledException) when (
                        stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            healthState.Stopped(timeProvider.GetUtcNow());
        }
    }

    private async Task RunConnectionAsync(
        int connectionAttempt,
        CancellationToken stoppingToken)
    {
        healthState.Authorizing(connectionAttempt);
        var authorizedUri = await marketDataProvider.GetLiveFeedAuthorizedUriAsync(
            stoppingToken);

        if (!authorizedUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Upstox live-feed authorization did not return a secure WebSocket URI.");
        }

        await using var connection = connectionFactory.Create();
        healthState.Connecting();

        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                   stoppingToken))
        {
            connectTimeout.CancelAfter(
                TimeSpan.FromSeconds(options.ConnectTimeoutSeconds));
            await connection.ConnectAsync(authorizedUri, connectTimeout.Token);
        }

        healthState.Connected(timeProvider.GetUtcNow());
        var subscription = await subscriptionProvider.GetSubscriptionAsync(stoppingToken);
        var command = commandBuilder.BuildSubscribe(subscription);
        await connection.SendAsync(
            command.Payload,
            WebSocketMessageType.Binary,
            endOfMessage: true,
            stoppingToken);
        healthState.Subscribed();

        logger.LogInformation(
            "Connected to Upstox V3 feed and subscribed to {InstrumentCount} " +
            "instrument keys in {Mode} mode. Subscription version {Version}.",
            subscription.InstrumentKeys.Count,
            subscription.Mode,
            subscription.Version);

        try
        {
            await ReceiveLoopAsync(
                connection,
                Guid.NewGuid().ToString("D"),
                stoppingToken);
        }
        finally
        {
            if (connection.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await connection.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "ThesisPulse Market Data worker stopping connection",
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogDebug(
                        exception,
                        "Upstox WebSocket close handshake did not complete cleanly.");
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(
        IUpstoxWebSocketConnection connection,
        string connectionCorrelationId,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var payload = await ReceiveMessageAsync(connection, stoppingToken);
            var receivedAtUtc = timeProvider.GetUtcNow();
            var envelope = decoder.Decode(payload);
            healthState.MessageReceived(
                receivedAtUtc,
                envelope.MessageType,
                envelope.SegmentStatuses);

            if (envelope.Feeds.Count == 0)
            {
                continue;
            }

            var updates = normalizer.Normalize(envelope.Feeds, receivedAtUtc);
            if (updates.Count == 0)
            {
                continue;
            }

            var correlationId =
                $"{connectionCorrelationId}:{envelope.CurrentTimestampMilliseconds}";
            var result = await marketDataStore.PersistLiveUpdatesAsync(
                updates,
                correlationId,
                stoppingToken);
            healthState.Persisted(timeProvider.GetUtcNow(), result.Accepted);

            if (result.Rejected > 0)
            {
                logger.LogWarning(
                    "Upstox live feed persistence accepted {Accepted}, ignored " +
                    "{Duplicates} duplicates and rejected {Rejected} updates.",
                    result.Accepted,
                    result.Duplicates,
                    result.Rejected);
            }
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReceiveMessageAsync(
        IUpstoxWebSocketConnection connection,
        CancellationToken stoppingToken)
    {
        var snapshot = healthState.GetSnapshot();
        var timeoutSeconds = snapshot.HasOpenMarketSegment
            ? options.MessageSilenceTimeoutSeconds
            : options.ClosedMarketMessageSilenceTimeoutSeconds;

        using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken);
        receiveTimeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var buffer = new byte[options.ReceiveBufferBytes];
        using var message = new MemoryStream();
        WebSocketMessageType? messageType = null;

        try
        {
            while (true)
            {
                var result = await connection.ReceiveAsync(
                    buffer,
                    receiveTimeout.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException(
                        $"Upstox closed the feed: {result.CloseStatus} " +
                        $"{result.CloseStatusDescription}".Trim());
                }

                messageType ??= result.MessageType;
                if (messageType != result.MessageType)
                {
                    throw new InvalidDataException(
                        "Upstox changed WebSocket message type within one fragmented message.");
                }

                if (message.Length + result.Count > options.MaximumMessageBytes)
                {
                    throw new InvalidDataException(
                        "Upstox feed message exceeded the configured maximum size.");
                }

                if (result.Count > 0)
                {
                    await message.WriteAsync(
                        buffer.AsMemory(0, result.Count),
                        receiveTimeout.Token);
                }

                if (result.EndOfMessage)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No Upstox feed message was received for {timeoutSeconds} seconds.");
        }

        var payload = message.ToArray();
        if (messageType == WebSocketMessageType.Text)
        {
            var text = Encoding.UTF8.GetString(payload);
            throw new InvalidDataException(
                $"Upstox returned an unexpected text frame: " +
                text[..Math.Min(text.Length, 500)]);
        }

        if (messageType != WebSocketMessageType.Binary || payload.Length == 0)
        {
            throw new InvalidDataException(
                "Upstox feed messages must be non-empty binary frames.");
        }

        return payload;
    }

    private TimeSpan CalculateReconnectDelay(int consecutiveFailures)
    {
        var exponent = Math.Min(consecutiveFailures - 1, 16);
        var exponentialDelay =
            options.InitialReconnectDelayMilliseconds * Math.Pow(2, exponent);
        var cappedDelay = Math.Min(
            options.MaximumReconnectDelayMilliseconds,
            exponentialDelay);
        var jitterMultiplier = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromMilliseconds(cappedDelay * jitterMultiplier);
    }
}
