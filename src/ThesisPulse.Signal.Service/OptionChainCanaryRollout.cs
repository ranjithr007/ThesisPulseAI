using System.Security.Cryptography;
using System.Text;

namespace ThesisPulse.Signal.Service;

public enum OptionChainRolloutMode
{
    Disabled,
    Canary,
    Expanded,
    RolledBack,
}

public sealed record OptionChainCanaryOptions
{
    public string Mode { get; init; } = "DISABLED";
    public int Percentage { get; init; }
    public string[] InstrumentAllowlist { get; init; } = [];
    public int MinimumSampleSize { get; init; } = 20;
    public decimal MinimumSuccessRate { get; init; } = 0.90m;
    public decimal MaximumRetryRate { get; init; } = 0.20m;
    public decimal MaximumTerminalFailureRate { get; init; } = 0.05m;
    public decimal MaximumDuplicateRate { get; init; } = 0.50m;

    public OptionChainRolloutMode ParsedMode => Mode.Trim().ToUpperInvariant() switch
    {
        "CANARY" => OptionChainRolloutMode.Canary,
        "EXPANDED" => OptionChainRolloutMode.Expanded,
        "ROLLED_BACK" => OptionChainRolloutMode.RolledBack,
        _ => OptionChainRolloutMode.Disabled,
    };

    public void Validate()
    {
        if (Percentage is < 0 or > 100)
            throw new InvalidOperationException("Option-chain canary percentage must be between 0 and 100.");
        if (MinimumSampleSize < 1)
            throw new InvalidOperationException("Option-chain canary minimum sample size must be positive.");
        if (MinimumSuccessRate is < 0 or > 1 || MaximumRetryRate is < 0 or > 1 ||
            MaximumTerminalFailureRate is < 0 or > 1 || MaximumDuplicateRate is < 0 or > 1)
            throw new InvalidOperationException("Option-chain rollout rates must be between 0 and 1.");
    }
}

public sealed record OptionChainCanaryDecision(
    bool Admitted,
    string Reason,
    int Bucket,
    string Mode,
    bool SelectionAuthority,
    bool ExecutionAuthority);

public sealed class OptionChainCanaryAdmission(OptionChainCanaryOptions options)
{
    public OptionChainCanaryDecision Evaluate(string instrumentKey, DateOnly expiryDate)
    {
        options.Validate();
        var mode = options.ParsedMode;
        if (mode is OptionChainRolloutMode.Disabled or OptionChainRolloutMode.RolledBack)
            return new(false, mode == OptionChainRolloutMode.RolledBack ? "ROLLOUT_ROLLED_BACK" : "ROLLOUT_DISABLED", -1, mode.ToString().ToUpperInvariant(), false, false);

        if (options.InstrumentAllowlist.Any(item => string.Equals(item, instrumentKey, StringComparison.OrdinalIgnoreCase)))
            return new(true, "INSTRUMENT_ALLOWLIST", 0, mode.ToString().ToUpperInvariant(), false, false);

        if (mode == OptionChainRolloutMode.Expanded)
            return new(true, "EXPANDED_ROLLOUT", 0, "EXPANDED", false, false);

        var bucket = ComputeBucket(instrumentKey, expiryDate);
        var admitted = bucket < options.Percentage;
        return new(admitted, admitted ? "CANARY_BUCKET_ADMITTED" : "CANARY_BUCKET_EXCLUDED", bucket, "CANARY", false, false);
    }

    private static int ComputeBucket(string instrumentKey, DateOnly expiryDate)
    {
        var input = Encoding.UTF8.GetBytes($"{instrumentKey.Trim().ToUpperInvariant()}|{expiryDate:yyyy-MM-dd}");
        var hash = SHA256.HashData(input);
        return BitConverter.ToUInt32(hash, 0) % 100 is var value ? (int)value : 0;
    }
}

public sealed record OptionChainGuardrailEvaluation(
    bool Healthy,
    bool RollbackRequired,
    long SampleSize,
    decimal SuccessRate,
    decimal RetryRate,
    decimal TerminalFailureRate,
    decimal DuplicateRate,
    IReadOnlyCollection<string> Breaches,
    bool SelectionAuthority,
    bool ExecutionAuthority,
    DateTimeOffset ObservedAtUtc);

public sealed class OptionChainGuardrailEvaluator(OptionChainCanaryOptions options)
{
    public OptionChainGuardrailEvaluation Evaluate(OptionChainExecutionMetrics metrics, DateTimeOffset observedAtUtc)
    {
        options.Validate();
        var terminalFailures = metrics.Failed + metrics.Rejected;
        var sample = metrics.Completed + metrics.Duplicates + metrics.RetryScheduled + terminalFailures;
        var denominator = sample == 0 ? 1m : sample;
        var successRate = metrics.Completed / denominator;
        var retryRate = metrics.RetryScheduled / denominator;
        var failureRate = terminalFailures / denominator;
        var duplicateRate = metrics.Duplicates / denominator;
        var breaches = new List<string>();

        if (sample >= options.MinimumSampleSize)
        {
            if (successRate < options.MinimumSuccessRate) breaches.Add("SUCCESS_RATE_BELOW_MINIMUM");
            if (retryRate > options.MaximumRetryRate) breaches.Add("RETRY_RATE_ABOVE_MAXIMUM");
            if (failureRate > options.MaximumTerminalFailureRate) breaches.Add("TERMINAL_FAILURE_RATE_ABOVE_MAXIMUM");
            if (duplicateRate > options.MaximumDuplicateRate) breaches.Add("DUPLICATE_RATE_ABOVE_MAXIMUM");
        }

        return new(
            Healthy: breaches.Count == 0,
            RollbackRequired: breaches.Count > 0,
            SampleSize: sample,
            SuccessRate: decimal.Round(successRate, 4),
            RetryRate: decimal.Round(retryRate, 4),
            TerminalFailureRate: decimal.Round(failureRate, 4),
            DuplicateRate: decimal.Round(duplicateRate, 4),
            Breaches: breaches,
            SelectionAuthority: false,
            ExecutionAuthority: false,
            ObservedAtUtc: observedAtUtc);
    }
}

public static class OptionChainCanaryEndpoints
{
    public static IEndpointRouteBuilder MapOptionChainCanaryRollout(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/internal/option-chain/rollout/status", (
            OptionChainCanaryOptions options,
            OptionChainExecutionState state) =>
        {
            var metrics = state.Snapshot(DateTimeOffset.UtcNow);
            var evaluation = new OptionChainGuardrailEvaluator(options).Evaluate(metrics, DateTimeOffset.UtcNow);
            return Results.Ok(new
            {
                mode = options.ParsedMode.ToString().ToUpperInvariant(),
                options.Percentage,
                options.InstrumentAllowlist,
                guardrails = evaluation,
                selectionAuthority = false,
                executionAuthority = false,
            });
        });

        endpoints.MapGet("/api/v1/internal/option-chain/rollout/admission", (
            string instrumentKey,
            DateOnly expiryDate,
            OptionChainCanaryOptions options) =>
            Results.Ok(new OptionChainCanaryAdmission(options).Evaluate(instrumentKey, expiryDate)));

        endpoints.MapPost("/api/v1/internal/option-chain/rollout/evaluate", (
            OptionChainCanaryOptions options,
            OptionChainExecutionState state) =>
        {
            var evaluation = new OptionChainGuardrailEvaluator(options)
                .Evaluate(state.Snapshot(DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
            return evaluation.RollbackRequired
                ? Results.Json(evaluation, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(evaluation);
        });

        return endpoints;
    }
}

public static class OptionChainCanaryRegistration
{
    public static IServiceCollection AddOptionChainCanaryRollout(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration.GetSection("OptionChainCanary").Get<OptionChainCanaryOptions>() ?? new();
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<OptionChainCanaryAdmission>();
        services.AddSingleton<OptionChainGuardrailEvaluator>();
        return services;
    }
}
