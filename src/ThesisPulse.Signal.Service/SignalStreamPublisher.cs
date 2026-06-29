using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Infrastructure.Signals;

namespace ThesisPulse.Signal.Service;

public enum SignalStreamPublishOutcome
{
    Disabled = 0,
    Published = 1,
    Failed = 2,
}

public sealed record SignalStreamPublishResult(
    SignalStreamPublishOutcome Outcome,
    string? Error);

public interface ISignalStreamPublisher
{
    Task<SignalStreamPublishResult> PublishAsync(
        StoredSignal signal,
        int statusSequence,
        DateTimeOffset occurredAtUtc,
        string correlationId,
        CancellationToken cancellationToken = default);
}

public sealed class DisabledSignalStreamPublisher : ISignalStreamPublisher
{
    public Task<SignalStreamPublishResult> PublishAsync(
        StoredSignal signal,
        int statusSequence,
        DateTimeOffset occurredAtUtc,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SignalStreamPublishResult(
            SignalStreamPublishOutcome.Disabled,
            Error: null));
    }
}

public sealed record SignalRealtimeOptions
{
    public required Uri TradingApiBaseUrl { get; init; }

    public required string InternalApiKey { get; init; }

    public int TimeoutSeconds { get; init; } = 5;
}

public sealed class TradingApiSignalStreamPublisher(
    HttpClient httpClient,
    SignalRealtimeOptions options,
    ILogger<TradingApiSignalStreamPublisher> logger) : ISignalStreamPublisher
{
    public async Task<SignalStreamPublishResult> PublishAsync(
        StoredSignal signal,
        int statusSequence,
        DateTimeOffset occurredAtUtc,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var streamEvent = new SignalStreamEventV1(
            EventUid: Guid.NewGuid(),
            EventType: SignalStreamContractV1.EventType,
            ContractVersion: SignalStreamContractV1.ContractVersion,
            SignalUid: signal.SignalUid,
            SignalId: signal.SignalId,
            InstrumentKey: signal.InstrumentKey,
            Direction: signal.Direction,
            PrimaryTimeframe: signal.PrimaryTimeframe,
            Strength: signal.Strength,
            Confidence: signal.Confidence,
            Status: signal.Status,
            StatusSequence: statusSequence,
            GeneratedAtUtc: signal.GeneratedAtUtc,
            ValidUntilUtc: signal.ValidUntilUtc,
            OccurredAtUtc: occurredAtUtc,
            CorrelationId: correlationId);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/internal/v1/signals/events")
            {
                Content = JsonContent.Create(streamEvent),
            };
            request.Headers.Add("X-ThesisPulse-Internal-Key", options.InternalApiKey);
            request.Headers.Add("X-Correlation-ID", correlationId);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new SignalStreamPublishResult(
                    SignalStreamPublishOutcome.Published,
                    Error: null);
            }

            var error = $"Trading API returned HTTP {(int)response.StatusCode}.";
            logger.LogWarning(
                "Signal stream publication failed for {SignalUid}: {Error}",
                signal.SignalUid,
                error);
            return new SignalStreamPublishResult(
                SignalStreamPublishOutcome.Failed,
                error);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Signal stream publication failed for {SignalUid}",
                signal.SignalUid);
            return new SignalStreamPublishResult(
                SignalStreamPublishOutcome.Failed,
                exception.Message);
        }
    }
}

public static class SignalStreamPublisherServiceCollectionExtensions
{
    public static IServiceCollection AddSignalStreamPublisher(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var enabled = configuration.GetValue("SignalRealtime:Enabled", false);
        if (!enabled)
        {
            services.TryAddSingleton<ISignalStreamPublisher, DisabledSignalStreamPublisher>();
            return services;
        }

        var baseUrl = configuration["SignalRealtime:TradingApiBaseUrl"];
        var apiKey = configuration["SignalRealtime:InternalApiKey"];
        var timeoutSeconds = configuration.GetValue("SignalRealtime:TimeoutSeconds", 5);

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException(
                "SignalRealtime:TradingApiBaseUrl must be an absolute URI.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "SignalRealtime:InternalApiKey is required when real-time publication is enabled.");
        }

        if (timeoutSeconds < 1)
        {
            throw new InvalidOperationException(
                "SignalRealtime:TimeoutSeconds must be at least one.");
        }

        var options = new SignalRealtimeOptions
        {
            TradingApiBaseUrl = baseUri,
            InternalApiKey = apiKey,
            TimeoutSeconds = timeoutSeconds,
        };

        services.AddSingleton(options);
        services.AddHttpClient<ISignalStreamPublisher, TradingApiSignalStreamPublisher>(client =>
        {
            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        });
        return services;
    }
}
