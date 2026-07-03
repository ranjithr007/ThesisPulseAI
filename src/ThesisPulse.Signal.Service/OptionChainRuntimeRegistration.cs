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

    public void Validate()
    {
        if (!Enabled)
            return;

        if (!string.Equals(PersistenceProvider, "SqlServer", StringComparison.Ordinal))
            throw new InvalidOperationException("Option-chain runtime currently supports only SqlServer persistence.");
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        if (MaximumAgeSeconds is < 1 or > 3600)
            throw new InvalidOperationException("Option-chain maximum age must be between 1 and 3600 seconds.");
        if (MinimumConfidence is < 0m or > 1m)
            throw new InvalidOperationException("Option-chain minimum confidence must be between 0 and 1.");
        if (CommandTimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("Option-chain command timeout must be between 1 and 300 seconds.");
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

        services.AddSingleton(runtime);
        services.AddSingleton(new OptionChainFusionEvidenceOptions
        {
            MaximumAge = TimeSpan.FromSeconds(runtime.MaximumAgeSeconds),
            MinimumConfidence = runtime.MinimumConfidence,
        });

        if (!runtime.Enabled)
        {
            services.AddSingleton<IOptionChainIntelligenceOutputStore, DisabledOptionChainOutputStore>();
        }
        else
        {
            services.AddSingleton(new SqlServerOptionChainIntelligenceOutputStoreOptions
            {
                ConnectionString = runtime.ConnectionString,
                CommandTimeoutSeconds = runtime.CommandTimeoutSeconds,
            });
            services.AddSingleton<IOptionChainIntelligenceOutputStore, SqlServerOptionChainIntelligenceOutputStore>();
        }

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
    IOptionChainIntelligenceOutputStore store) : IHealthCheck
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
