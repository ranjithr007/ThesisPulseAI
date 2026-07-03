using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ThesisPulse.Shared.Contracts.Intelligence.V1;

namespace ThesisPulse.Signal.Service;

public sealed record OptionChainSnapshotDispatchEntryV1(
    Guid DerivativeContractUid,
    string InstrumentKey,
    DateOnly ExpiryDate,
    decimal StrikePrice,
    string OptionType,
    decimal? LastPrice,
    decimal? VolumeQuantity,
    decimal? OpenInterest,
    decimal? ImpliedVolatility,
    decimal? Delta,
    decimal? ContractMultiplier,
    string QualityStatus,
    string? GreeksSourceVersion);

public sealed record OptionChainSnapshotDispatchV1(
    Guid SourceMessageUid,
    Guid SnapshotUid,
    string UnderlyingInstrumentKey,
    DateOnly ExpiryDate,
    DateTimeOffset EventAtUtc,
    DateTimeOffset ReceivedAtUtc,
    decimal UnderlyingPrice,
    string SnapshotStatus,
    string QualityStatus,
    bool IsPointInTimeEligible,
    int Revision,
    IReadOnlyCollection<OptionChainSnapshotDispatchEntryV1> Entries,
    string? CalculationSourceVersion);

public interface IOptionChainSnapshotDispatchSource
{
    Task<OptionChainSnapshotDispatchV1?> LoadAsync(
        OptionChainWorkItem workItem,
        CancellationToken cancellationToken = default);
}

public sealed record OptionChainPythonWorkerOptions
{
    public bool Enabled { get; init; }

    public string ServiceBaseUrl { get; init; } = "http://localhost:8000";

    public string InternalApiKey { get; init; } = string.Empty;

    public int RequestTimeoutSeconds { get; init; } = 30;

    public int PollIntervalSeconds { get; init; } = 5;

    public int BatchSize { get; init; } = 25;

    public void Validate()
    {
        if (RequestTimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("Option-chain Python request timeout must be between 1 and 300 seconds.");
        if (PollIntervalSeconds is < 1 or > 900)
            throw new InvalidOperationException("Option-chain execution poll interval must be between 1 and 900 seconds.");
        if (BatchSize is < 1 or > 250)
            throw new InvalidOperationException("Option-chain execution batch size must be between 1 and 250.");
        if (!Enabled)
            return;
        if (!Uri.TryCreate(ServiceBaseUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("Option-chain Python service base URL must be absolute.");
        ArgumentException.ThrowIfNullOrWhiteSpace(InternalApiKey);
    }
}

public sealed record OptionChainExecutionMetrics(
    long Runs,
    long Idle,
    long Completed,
    long Duplicates,
    long Rejected,
    long RetryScheduled,
    long Failed,
    DateTimeOffset? LastRunAtUtc,
    DateTimeOffset ObservedAtUtc);

public sealed class OptionChainExecutionState
{
    private long _runs;
    private long _idle;
    private long _completed;
    private long _duplicates;
    private long _rejected;
    private long _retryScheduled;
    private long _failed;
    private long _lastRunTicks;

    public void Record(OptionChainWorkerRunResult result, DateTimeOffset observedAtUtc)
    {
        Interlocked.Increment(ref _runs);
        Interlocked.Exchange(ref _lastRunTicks, observedAtUtc.UtcTicks);
        if (!result.WorkFound)
        {
            Interlocked.Increment(ref _idle);
            return;
        }

        switch (result.Outcome)
        {
            case OptionChainWorkExecutionOutcome.Completed:
                Interlocked.Increment(ref _completed);
                break;
            case OptionChainWorkExecutionOutcome.Duplicate:
                Interlocked.Increment(ref _duplicates);
                break;
            case OptionChainWorkExecutionOutcome.Rejected:
                Interlocked.Increment(ref _rejected);
                break;
            case OptionChainWorkExecutionOutcome.RetryableFailure when result.RetryAtUtc.HasValue:
                Interlocked.Increment(ref _retryScheduled);
                break;
            case OptionChainWorkExecutionOutcome.RetryableFailure:
            case OptionChainWorkExecutionOutcome.TerminalFailure:
                Interlocked.Increment(ref _failed);
                break;
        }
    }

    public OptionChainExecutionMetrics Snapshot(DateTimeOffset observedAtUtc)
    {
        var ticks = Interlocked.Read(ref _lastRunTicks);
        return new OptionChainExecutionMetrics(
            Interlocked.Read(ref _runs),
            Interlocked.Read(ref _idle),
            Interlocked.Read(ref _completed),
            Interlocked.Read(ref _duplicates),
            Interlocked.Read(ref _rejected),
            Interlocked.Read(ref _retryScheduled),
            Interlocked.Read(ref _failed),
            ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero),
            observedAtUtc);
    }
}

public sealed class OptionChainPythonWorkHandler(
    IOptionChainSnapshotDispatchSource snapshotSource,
    IHttpClientFactory httpClientFactory,
    OptionChainPythonWorkerOptions options,
    ILogger<OptionChainPythonWorkHandler> logger) : IOptionChainWorkHandler
{
    public const string HttpClientName = "OptionChainPythonRuntime";
    private const string ProcessingPath = "/internal/v1/market-data/option-chain";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OptionChainWorkExecutionResult> HandleAsync(
        OptionChainWorkItem workItem,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (!options.Enabled)
            return new(OptionChainWorkExecutionOutcome.TerminalFailure, "OPTION_CHAIN_EXECUTION_DISABLED");

        OptionChainSnapshotDispatchV1? snapshot;
        try
        {
            snapshot = await snapshotSource.LoadAsync(workItem, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Option-chain snapshot load failed for work {WorkUid}",
                workItem.WorkUid);
            return new(OptionChainWorkExecutionOutcome.RetryableFailure, "SNAPSHOT_LOAD_FAILED");
        }

        if (snapshot is null)
            return new(OptionChainWorkExecutionOutcome.Rejected, "SOURCE_SNAPSHOT_NOT_FOUND_OR_INELIGIBLE");
        if (snapshot.SourceMessageUid != workItem.WorkUid ||
            snapshot.SnapshotUid != workItem.SnapshotUid ||
            !string.Equals(snapshot.UnderlyingInstrumentKey, workItem.InstrumentKey, StringComparison.OrdinalIgnoreCase))
        {
            return new(OptionChainWorkExecutionOutcome.TerminalFailure, "SOURCE_SNAPSHOT_LINEAGE_MISMATCH");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ProcessingPath)
        {
            Content = JsonContent.Create(snapshot, options: JsonOptions),
        };
        request.Headers.Add("X-ThesisPulse-Internal-Key", options.InternalApiKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(OptionChainWorkExecutionOutcome.RetryableFailure, "PYTHON_REQUEST_TIMEOUT");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                exception,
                "Option-chain Python request failed for work {WorkUid}",
                workItem.WorkUid);
            return new(OptionChainWorkExecutionOutcome.RetryableFailure, "PYTHON_TRANSPORT_FAILURE");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return await MapFailureAsync(response, cancellationToken);

            OptionChainProcessingResultV1? processing;
            try
            {
                processing = await response.Content.ReadFromJsonAsync<OptionChainProcessingResultV1>(
                    JsonOptions,
                    cancellationToken);
            }
            catch (JsonException exception)
            {
                logger.LogError(
                    exception,
                    "Option-chain Python response was malformed for work {WorkUid}",
                    workItem.WorkUid);
                return new(OptionChainWorkExecutionOutcome.TerminalFailure, "PYTHON_RESPONSE_MALFORMED");
            }

            if (processing is null)
                return new(OptionChainWorkExecutionOutcome.TerminalFailure, "PYTHON_RESPONSE_EMPTY");

            return MapProcessingResult(workItem, processing);
        }
    }

    private static OptionChainWorkExecutionResult MapProcessingResult(
        OptionChainWorkItem workItem,
        OptionChainProcessingResultV1 processing)
    {
        return processing.Outcome switch
        {
            "CREATED" or "REVISED" => ValidateCompleted(workItem, processing),
            "DUPLICATE" => ValidateDuplicate(workItem, processing),
            "IGNORED_INELIGIBLE" => new(
                OptionChainWorkExecutionOutcome.Rejected,
                processing.Reason ?? "PYTHON_IGNORED_INELIGIBLE"),
            _ => new(
                OptionChainWorkExecutionOutcome.TerminalFailure,
                "PYTHON_OUTCOME_UNSUPPORTED"),
        };
    }

    private static OptionChainWorkExecutionResult ValidateCompleted(
        OptionChainWorkItem workItem,
        OptionChainProcessingResultV1 processing)
    {
        var validation = ValidateOutput(workItem, processing.Output);
        return validation is null
            ? new(OptionChainWorkExecutionOutcome.Completed)
            : new(OptionChainWorkExecutionOutcome.TerminalFailure, validation);
    }

    private static OptionChainWorkExecutionResult ValidateDuplicate(
        OptionChainWorkItem workItem,
        OptionChainProcessingResultV1 processing)
    {
        if (processing.Output is null)
            return new(OptionChainWorkExecutionOutcome.Duplicate, processing.Reason);
        var validation = ValidateOutput(workItem, processing.Output);
        return validation is null
            ? new(OptionChainWorkExecutionOutcome.Duplicate, processing.Reason)
            : new(OptionChainWorkExecutionOutcome.TerminalFailure, validation);
    }

    private static string? ValidateOutput(
        OptionChainWorkItem workItem,
        OptionChainIntelligenceOutputV1? output)
    {
        if (output is null)
            return "PYTHON_OUTPUT_REQUIRED";
        if (!string.Equals(output.EngineCode, OptionChainIntelligenceContractV1.EngineCode, StringComparison.Ordinal))
            return "PYTHON_ENGINE_CODE_MISMATCH";
        if (!string.Equals(output.EngineVersion, workItem.EngineVersion, StringComparison.Ordinal))
            return "PYTHON_ENGINE_VERSION_MISMATCH";
        if (!string.Equals(output.PolicyVersion, workItem.PolicyVersion, StringComparison.Ordinal))
            return "PYTHON_POLICY_VERSION_MISMATCH";
        if (!string.Equals(output.UnderlyingInstrumentKey, workItem.InstrumentKey, StringComparison.OrdinalIgnoreCase))
            return "PYTHON_INSTRUMENT_LINEAGE_MISMATCH";
        if (!output.SourceSnapshotUids.Contains(workItem.SnapshotUid))
            return "PYTHON_SNAPSHOT_LINEAGE_MISMATCH";
        if (output.AsOfUtc > workItem.WorkflowCutoffUtc ||
            output.GeneratedAtUtc < output.AsOfUtc)
            return "PYTHON_POINT_IN_TIME_MISMATCH";
        if (output.SelectionAuthority || output.ExecutionAuthority)
            return "PYTHON_AUTHORITY_DRIFT";
        return null;
    }

    private static async Task<OptionChainWorkExecutionResult> MapFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var detail = await ReadFailureDetailAsync(response, cancellationToken);
        var reason = $"PYTHON_HTTP_{(int)response.StatusCode}:{detail}";

        if (response.StatusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.Conflict or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.BadGateway or
            HttpStatusCode.GatewayTimeout or
            HttpStatusCode.InternalServerError)
        {
            return new(OptionChainWorkExecutionOutcome.RetryableFailure, reason);
        }

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            return new(OptionChainWorkExecutionOutcome.Rejected, reason);

        return new(OptionChainWorkExecutionOutcome.TerminalFailure, reason);
    }

    private static async Task<string> ReadFailureDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var value = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
            return response.ReasonPhrase ?? "ERROR";
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 256 ? value : value[..256];
    }
}

public sealed class OptionChainExecutionWorker(
    OptionChainPythonWorkerOptions options,
    OptionChainWorkerOrchestrator orchestrator,
    OptionChainExecutionState state,
    TimeProvider timeProvider,
    ILogger<OptionChainExecutionWorker> logger) : BackgroundService
{
    private readonly string _leaseOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:option-chain";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        if (!options.Enabled)
            return;

        var delay = TimeSpan.FromSeconds(options.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                for (var index = 0; index < options.BatchSize; index++)
                {
                    var result = await orchestrator.RunOnceAsync(_leaseOwner, stoppingToken);
                    state.Record(result, timeProvider.GetUtcNow());
                    if (!result.WorkFound)
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Option-chain execution polling failed closed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}

internal sealed class DisabledOptionChainSnapshotDispatchSource : IOptionChainSnapshotDispatchSource
{
    public Task<OptionChainSnapshotDispatchV1?> LoadAsync(
        OptionChainWorkItem workItem,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<OptionChainSnapshotDispatchV1?>(null);
}
