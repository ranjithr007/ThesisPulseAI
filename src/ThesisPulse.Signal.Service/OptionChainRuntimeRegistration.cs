using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ThesisPulse.Signal.Service;

public sealed record OptionChainRuntimeOptions
{
    public const string SectionName = "OptionChainRuntime";

    public bool Enabled { get; init; }

    public string PersistenceProvider { get; init; } = "SqlServer";

    public string ConnectionString { get; init; } = string.Empty;

    public int MaximumAgeSeconds { get; init; } = 420;

    public decimal MinimumConfidence { get; init; }

    public int CommandTimeoutSeconds { get; init; } = 30;

    public int WorkerMaximumAttempts { get; init; } = 5;

    public int WorkerLeaseSeconds { get; init; } = 120;

    public int WorkerInitialRetrySeconds { get; init; } = 10;

    public int WorkerMaximumRetrySeconds { get; init; } = 300;

    public bool WorkerDiscoveryEnabled { get; init; }

    public int WorkerPollIntervalSeconds { get; init; } = 30;

    public int WorkerBatchSize { get; init; } = 25;

    public int WorkerSnapshotMaximumAgeSeconds { get; init; } = 420;

    public bool WorkerExecutionEnabled { get; init; }

    public int WorkerExecutionPollIntervalSeconds { get; init; } = 5;

    public int WorkerExecutionBatchSize { get; init; } = 25;

    public string PythonServiceBaseUrl { get; init; } = "http://localhost:8000";

    public string PythonInternalApiKey { get; init; } = string.Empty;

    public int PythonRequestTimeoutSeconds { get; init; } = 30;

    public string PythonBrokerCode { get; init; } = "UPSTOX";

    public void Validate()
    {
        if (!Enabled)
        {
            if (WorkerDiscoveryEnabled || WorkerExecutionEnabled)
                throw new InvalidOperationException("Option-chain workers cannot be enabled while the runtime is disabled.");
            return;
        }

        if (!string.Equals(PersistenceProvider, "SqlServer", StringComparison.Ordinal))
            throw new InvalidOperationException("Option-chain runtime currently supports only SqlServer persistence.");
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        if (MaximumAgeSeconds is < 1 or > 3600)
            throw new InvalidOperationException("Option-chain maximum age must be between 1 and 3600 seconds.");
        if (MinimumConfidence is < 0m or > 1m)
            throw new InvalidOperationException("Option-chain minimum confidence must be between 0 and 1.");
        if (CommandTimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("Option-chain command timeout must be between 1 and 300 seconds.");
        if (WorkerMaximumAttempts is < 1 or > 20)
            throw new InvalidOperationException("Option-chain worker maximum attempts must be between 1 and 20.");
        if (WorkerLeaseSeconds is < 5 or > 3600)
            throw new InvalidOperationException("Option-chain worker lease must be between 5 and 3600 seconds.");
        if (WorkerInitialRetrySeconds is < 1 or > 3600 || WorkerMaximumRetrySeconds < WorkerInitialRetrySeconds)
            throw new InvalidOperationException("Option-chain worker retry settings are invalid.");
        if (WorkerPollIntervalSeconds is < 1 or > 900)
            throw new InvalidOperationException("Option-chain worker poll interval must be between 1 and 900 seconds.");
        if (WorkerBatchSize is < 1 or > 250)
            throw new InvalidOperationException("Option-chain worker batch size must be between 1 and 250.");
        if (WorkerSnapshotMaximumAgeSeconds is < 1 or > 86400)
            throw new InvalidOperationException("Option-chain worker snapshot maximum age must be between 1 and 86400 seconds.");
        if (WorkerExecutionPollIntervalSeconds is < 1 or > 900)
            throw new InvalidOperationException("Option-chain execution poll interval must be between 1 and 900 seconds.");
        if (WorkerExecutionBatchSize is < 1 or > 250)
            throw new InvalidOperationException("Option-chain execution batch size must be between 1 and 250.");
        if (PythonRequestTimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("Option-chain Python request timeout must be between 1 and 300 seconds.");
        ArgumentException.ThrowIfNullOrWhiteSpace(PythonBrokerCode);
        if (WorkerExecutionEnabled)
        {
            if (!Uri.TryCreate(PythonServiceBaseUrl, UriKind.Absolute, out _))
                throw new InvalidOperationException("Option-chain Python service base URL must be absolute.");
            ArgumentException.ThrowIfNullOrWhiteSpace(PythonInternalApiKey);
        }
    }
}

public static class OptionChainRuntimeRegistration
{
    public static IServiceCollection AddOptionChainFusionRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var runtime = configuration.GetSection(OptionChainRuntimeOptions.SectionName)
            .Get<OptionChainRuntimeOptions>() ?? new OptionChainRuntimeOptions();
        runtime.Validate();

        var intakeOptions = new OptionChainWorkIntakeOptions
        {
            Enabled = runtime.Enabled && runtime.WorkerDiscoveryEnabled,
            PollIntervalSeconds = runtime.WorkerPollIntervalSeconds,
            BatchSize = runtime.WorkerBatchSize,
            MaximumSnapshotAgeSeconds = runtime.WorkerSnapshotMaximumAgeSeconds,
        };
        intakeOptions.Validate();

        var pythonOptions = new OptionChainPythonWorkerOptions
        {
            Enabled = runtime.Enabled && runtime.WorkerExecutionEnabled,
            ServiceBaseUrl = runtime.PythonServiceBaseUrl,
            InternalApiKey = runtime.PythonInternalApiKey,
            RequestTimeoutSeconds = runtime.PythonRequestTimeoutSeconds,
            PollIntervalSeconds = runtime.WorkerExecutionPollIntervalSeconds,
            BatchSize = runtime.WorkerExecutionBatchSize,
        };
        pythonOptions.Validate();

        services.AddSingleton(runtime);
        services.AddSingleton(intakeOptions);
        services.AddSingleton(pythonOptions);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new OptionChainFusionEvidenceOptions
        {
            MaximumAge = TimeSpan.FromSeconds(runtime.MaximumAgeSeconds),
            MinimumConfidence = runtime.MinimumConfidence,
        });
        services.AddSingleton(new OptionChainWorkerPolicy
        {
            MaximumAttempts = runtime.WorkerMaximumAttempts,
            LeaseDuration = TimeSpan.FromSeconds(runtime.WorkerLeaseSeconds),
            InitialRetryDelay = TimeSpan.FromSeconds(runtime.WorkerInitialRetrySeconds),
            MaximumRetryDelay = TimeSpan.FromSeconds(runtime.WorkerMaximumRetrySeconds),
        });
        services.AddHttpClient(OptionChainPythonWorkHandler.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(pythonOptions.ServiceBaseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(pythonOptions.RequestTimeoutSeconds);
        });

        if (!runtime.Enabled)
        {
            services.AddSingleton<IOptionChainIntelligenceOutputStore, DisabledOptionChainOutputStore>();
            services.AddSingleton<IOptionChainWorkQueue, InMemoryOptionChainWorkQueue>();
            services.AddSingleton<IOptionChainWorkCandidateSource, EmptyOptionChainWorkCandidateSource>();
            services.AddSingleton<IOptionChainSnapshotDispatchSource, DisabledOptionChainSnapshotDispatchSource>();
        }
        else
        {
            services.AddSingleton(new SqlServerOptionChainIntelligenceOutputStoreOptions
            {
                ConnectionString = runtime.ConnectionString,
                CommandTimeoutSeconds = runtime.CommandTimeoutSeconds,
            });
            services.AddSingleton(new SqlServerOptionChainWorkQueueOptions
            {
                ConnectionString = runtime.ConnectionString,
                CommandTimeoutSeconds = runtime.CommandTimeoutSeconds,
            });
            services.AddSingleton<IOptionChainIntelligenceOutputStore, SqlServerOptionChainIntelligenceOutputStore>();
            services.AddSingleton<IOptionChainWorkQueue, SqlServerOptionChainWorkQueue>();

            if (runtime.WorkerDiscoveryEnabled)
            {
                services.AddSingleton(new SqlServerOptionChainWorkCandidateSourceOptions
                {
                    ConnectionString = runtime.ConnectionString,
                    CommandTimeoutSeconds = runtime.CommandTimeoutSeconds,
                });
                services.AddSingleton<IOptionChainWorkCandidateSource, SqlServerOptionChainWorkCandidateSource>();
            }
            else
            {
                services.AddSingleton<IOptionChainWorkCandidateSource, EmptyOptionChainWorkCandidateSource>();
            }

            if (runtime.WorkerExecutionEnabled)
            {
                services.AddSingleton(new SqlServerOptionChainSnapshotDispatchSourceOptions
                {
                    ConnectionString = runtime.ConnectionString,
                    BrokerCode = runtime.PythonBrokerCode,
                    CommandTimeoutSeconds = runtime.CommandTimeoutSeconds,
                });
                services.AddSingleton<IOptionChainSnapshotDispatchSource, SqlServerOptionChainSnapshotDispatchSource>();
            }
            else
            {
                services.AddSingleton<IOptionChainSnapshotDispatchSource, DisabledOptionChainSnapshotDispatchSource>();
            }
        }

        services.AddSingleton<OptionChainWorkIntakeState>();
        services.AddSingleton<OptionChainWorkIntake>();
        services.AddHostedService<OptionChainWorkIntakeWorker>();
        services.AddSingleton<OptionChainExecutionState>();
        services.AddSingleton<IOptionChainWorkHandler, OptionChainPythonWorkHandler>();
        services.AddSingleton<OptionChainWorkerOrchestrator>();
        services.AddHostedService<OptionChainExecutionWorker>();
        services.AddSingleton<IOptionChainFusionEvidenceProvider, OptionChainFusionEvidenceProvider>();
        services.AddSingleton<OptionChainFusionRequestComposer>();
        services.AddSingleton<OptionChainFusionRuntimeDiagnostics>();
        services.AddSingleton<OptionChainFusionRuntimeOrchestrator>();
        services.AddHealthChecks().AddCheck<OptionChainRuntimeHealthCheck>(
            "option-chain-runtime",
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready", "option-chain"]);
        return services;
    }
}

internal sealed class DisabledOptionChainOutputStore : IOptionChainIntelligenceOutputStore
{
    public Task<OptionChainAppendResult> AppendAsync(
        OptionChainPersistenceEnvelope envelope,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new OptionChainAppendResult(
            OptionChainAppendOutcome.Rejected,
            envelope.Output.OutputUid,
            envelope.Output.Revision,
            "OPTION_CHAIN_RUNTIME_DISABLED"));

    public Task<OptionChainPersistenceEnvelope?> GetLatestAtOrBeforeAsync(
        OptionChainPointInTimeQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<OptionChainPersistenceEnvelope?>(null);
}

public sealed class OptionChainRuntimeHealthCheck(
    OptionChainRuntimeOptions options,
    IOptionChainIntelligenceOutputStore store,
    IOptionChainWorkQueue queue,
    IOptionChainWorkCandidateSource candidateSource,
    IOptionChainSnapshotDispatchSource snapshotSource,
    IOptionChainWorkHandler workHandler) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            options.Validate();
            if (!options.Enabled)
                return Task.FromResult(HealthCheckResult.Healthy("Option-chain runtime is disabled by configuration."));
            if (store is not SqlServerOptionChainIntelligenceOutputStore)
                return Task.FromResult(HealthCheckResult.Unhealthy("Configured SQL Server option-chain store is not active."));
            if (queue is not SqlServerOptionChainWorkQueue)
                return Task.FromResult(HealthCheckResult.Unhealthy("Configured SQL Server option-chain queue is not active."));
            if (options.WorkerDiscoveryEnabled && candidateSource is not SqlServerOptionChainWorkCandidateSource)
                return Task.FromResult(HealthCheckResult.Unhealthy("Configured SQL Server option-chain discovery source is not active."));
            if (options.WorkerExecutionEnabled && snapshotSource is not SqlServerOptionChainSnapshotDispatchSource)
                return Task.FromResult(HealthCheckResult.Unhealthy("Configured SQL Server option-chain snapshot source is not active."));
            if (options.WorkerExecutionEnabled && workHandler is not OptionChainPythonWorkHandler)
                return Task.FromResult(HealthCheckResult.Unhealthy("Configured Python option-chain work handler is not active."));
            return Task.FromResult(HealthCheckResult.Healthy("Option-chain runtime configuration and DI wiring are valid."));
        }
        catch (Exception exception)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Option-chain runtime configuration is invalid.",
                exception));
        }
    }
}

public sealed record OptionChainRuntimeDiagnosticSnapshot(
    bool Enabled,
    string PersistenceProvider,
    int MaximumAgeSeconds,
    decimal MinimumConfidence,
    string Outcome,
    Guid? OutputUid,
    int? Revision,
    IReadOnlyCollection<string> Warnings,
    IReadOnlyCollection<string> GateFailures);

public sealed class OptionChainFusionRuntimeDiagnostics(OptionChainRuntimeOptions options)
{
    public OptionChainRuntimeDiagnosticSnapshot Snapshot(
        OptionChainFusionEvidenceResult result)
    {
        var outcome = result.GateFailures.Count > 0
            ? "HARD_FAILURE"
            : result.Evidence is not null
                ? "INCLUDED"
                : result.Warnings.Count > 0
                    ? "WARNING_ONLY"
                    : "NOT_AVAILABLE";

        return new OptionChainRuntimeDiagnosticSnapshot(
            options.Enabled,
            options.PersistenceProvider,
            options.MaximumAgeSeconds,
            options.MinimumConfidence,
            outcome,
            result.OutputUid,
            result.Revision,
            result.Warnings,
            result.GateFailures);
    }
}
