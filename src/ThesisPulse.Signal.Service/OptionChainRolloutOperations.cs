using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ThesisPulse.Signal.Service;

public sealed record OptionChainOperationsOptions
{
    public bool SchedulerEnabled { get; init; }
    public int GuardrailIntervalSeconds { get; init; } = 60;
    public int ReconciliationIntervalSeconds { get; init; } = 300;
    public int AuditRetentionDays { get; init; } = 90;
    public bool ReadOnlyStatusRequiresAuthentication { get; init; }
    public string OperatorApiKey { get; init; } = string.Empty;

    public void Validate()
    {
        if (GuardrailIntervalSeconds < 10 || ReconciliationIntervalSeconds < 30)
            throw new InvalidOperationException("Option-chain scheduler intervals are below the production minimum.");
        if (AuditRetentionDays < 7)
            throw new InvalidOperationException("Option-chain audit retention must be at least seven days.");
    }
}

public sealed record OptionChainRolloutAuditRecord(
    Guid AuditUid,
    Guid CorrelationUid,
    string CommandKey,
    string Actor,
    string Action,
    string PreviousMode,
    string NewMode,
    long PreviousVersion,
    long NewVersion,
    string Reason,
    string SourceService,
    DateTimeOffset ObservedAtUtc,
    bool SelectionAuthority,
    bool ExecutionAuthority);

public interface IOptionChainRolloutAuditStore
{
    ValueTask<OptionChainRolloutAuditRecord?> GetLatestAsync(CancellationToken cancellationToken);
    ValueTask<OptionChainRolloutAuditRecord?> FindByCommandKeyAsync(string commandKey, CancellationToken cancellationToken);
    ValueTask AppendAsync(OptionChainRolloutAuditRecord record, CancellationToken cancellationToken);
    ValueTask<IReadOnlyCollection<OptionChainRolloutAuditRecord>> GetRecentAsync(int limit, CancellationToken cancellationToken);
    ValueTask<int> DeleteExpiredAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}

public sealed class InMemoryOptionChainRolloutAuditStore : IOptionChainRolloutAuditStore
{
    private readonly object _sync = new();
    private readonly List<OptionChainRolloutAuditRecord> _records = [];

    public ValueTask<OptionChainRolloutAuditRecord?> GetLatestAsync(CancellationToken cancellationToken)
    {
        lock (_sync) return ValueTask.FromResult(_records.OrderByDescending(x => x.NewVersion).FirstOrDefault());
    }

    public ValueTask<OptionChainRolloutAuditRecord?> FindByCommandKeyAsync(string commandKey, CancellationToken cancellationToken)
    {
        lock (_sync) return ValueTask.FromResult(_records.FirstOrDefault(x => x.CommandKey == commandKey));
    }

    public ValueTask AppendAsync(OptionChainRolloutAuditRecord record, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_records.Any(x => x.CommandKey == record.CommandKey)) return ValueTask.CompletedTask;
            if (_records.Any(x => x.NewVersion == record.NewVersion))
                throw new InvalidOperationException("A rollout transition already exists for the requested version.");
            _records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<IReadOnlyCollection<OptionChainRolloutAuditRecord>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        lock (_sync)
            return ValueTask.FromResult<IReadOnlyCollection<OptionChainRolloutAuditRecord>>(
                _records.OrderByDescending(x => x.ObservedAtUtc).Take(Math.Clamp(limit, 1, 500)).ToArray());
    }

    public ValueTask<int> DeleteExpiredAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            var removed = _records.RemoveAll(x => x.ObservedAtUtc < cutoffUtc);
            return ValueTask.FromResult(removed);
        }
    }
}

public sealed record OptionChainOperatorCommand(
    string CommandKey,
    Guid CorrelationUid,
    long ExpectedVersion,
    string TargetMode,
    string Reason,
    string Actor);

public sealed record OptionChainDurableState(
    string Mode,
    long Version,
    bool WorkerExecutionSuppressed,
    DateTimeOffset? ChangedAtUtc,
    string? LastReason,
    bool RestoredFromAudit,
    bool SelectionAuthority,
    bool ExecutionAuthority);

public sealed class OptionChainDurableRolloutCoordinator(
    OptionChainRolloutState runtimeState,
    IOptionChainRolloutAuditStore auditStore)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _version;
    private bool _restored;

    public async ValueTask<OptionChainDurableState> RestoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var latest = await auditStore.GetLatestAsync(cancellationToken);
            if (latest is not null)
            {
                _version = latest.NewVersion;
                if (latest.NewMode == "ROLLED_BACK") runtimeState.Rollback(latest.Reason, latest.ObservedAtUtc);
                else runtimeState.Recover(latest.NewMode, latest.Reason, latest.ObservedAtUtc);
                _restored = true;
            }
            return Snapshot();
        }
        finally { _gate.Release(); }
    }

    public OptionChainDurableState Snapshot()
    {
        var state = runtimeState.Snapshot();
        return new(state.Mode, Interlocked.Read(ref _version), state.WorkerExecutionSuppressed, state.ChangedAtUtc,
            state.LastReason, _restored, false, false);
    }

    public async ValueTask<(bool Replayed, OptionChainDurableState State)> ApplyAsync(
        OptionChainOperatorCommand command,
        bool rollback,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.CommandKey))
            throw new InvalidOperationException("CommandKey is required.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await auditStore.FindByCommandKeyAsync(command.CommandKey, cancellationToken);
            if (existing is not null) return (true, Snapshot());
            if (command.ExpectedVersion != _version)
                throw new InvalidOperationException("STALE_ROLLOUT_VERSION");

            var previous = runtimeState.Snapshot();
            var observedAtUtc = DateTimeOffset.UtcNow;
            var next = rollback
                ? runtimeState.Rollback(command.Reason, observedAtUtc)
                : runtimeState.Recover(command.TargetMode, command.Reason, observedAtUtc);
            var newVersion = _version + 1;

            await auditStore.AppendAsync(new(
                Guid.NewGuid(),
                command.CorrelationUid == Guid.Empty ? Guid.NewGuid() : command.CorrelationUid,
                command.CommandKey,
                string.IsNullOrWhiteSpace(command.Actor) ? "UNKNOWN_OPERATOR" : command.Actor,
                rollback ? "ROLLBACK" : "RECOVERY",
                previous.Mode,
                next.Mode,
                _version,
                newVersion,
                command.Reason,
                "ThesisPulse.Signal.Service",
                observedAtUtc,
                false,
                false), cancellationToken);

            _version = newVersion;
            return (false, Snapshot());
        }
        finally { _gate.Release(); }
    }
}

public sealed class OptionChainScheduleLease
{
    private int _guardrailRunning;
    private int _reconciliationRunning;

    public bool TryAcquireGuardrail() => Interlocked.CompareExchange(ref _guardrailRunning, 1, 0) == 0;
    public void ReleaseGuardrail() => Volatile.Write(ref _guardrailRunning, 0);
    public bool TryAcquireReconciliation() => Interlocked.CompareExchange(ref _reconciliationRunning, 1, 0) == 0;
    public void ReleaseReconciliation() => Volatile.Write(ref _reconciliationRunning, 0);
}

public static class OptionChainOperatorAuthorization
{
    public static bool IsAuthorized(HttpRequest request, OptionChainOperationsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OperatorApiKey)) return false;
        if (!request.Headers.TryGetValue("X-ThesisPulse-Operator-Key", out var supplied)) return false;
        var expectedBytes = Encoding.UTF8.GetBytes(options.OperatorApiKey);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied.ToString());
        return expectedBytes.Length == suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}

public static class OptionChainOperationsEndpoints
{
    public static IEndpointRouteBuilder MapOptionChainOperations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/internal/option-chain/operations/state", async (
            HttpRequest request,
            OptionChainOperationsOptions options,
            OptionChainDurableRolloutCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            if (options.ReadOnlyStatusRequiresAuthentication && !OptionChainOperatorAuthorization.IsAuthorized(request, options))
                return Results.Unauthorized();
            return Results.Ok(await coordinator.RestoreAsync(cancellationToken));
        });

        endpoints.MapGet("/api/v1/internal/option-chain/operations/audit", async (
            HttpRequest request,
            int? limit,
            OptionChainOperationsOptions options,
            IOptionChainRolloutAuditStore store,
            CancellationToken cancellationToken) =>
        {
            if (!OptionChainOperatorAuthorization.IsAuthorized(request, options)) return Results.Unauthorized();
            return Results.Ok(await store.GetRecentAsync(limit ?? 100, cancellationToken));
        });

        endpoints.MapPost("/api/v1/internal/option-chain/operations/rollback", async (
            HttpRequest request,
            OptionChainOperatorCommand command,
            OptionChainOperationsOptions options,
            OptionChainDurableRolloutCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            if (!OptionChainOperatorAuthorization.IsAuthorized(request, options)) return Results.Unauthorized();
            try
            {
                var result = await coordinator.ApplyAsync(command, true, cancellationToken);
                return Results.Ok(new { outcome = result.Replayed ? "REPLAYED" : "APPLIED", result.State });
            }
            catch (InvalidOperationException ex) when (ex.Message == "STALE_ROLLOUT_VERSION")
            {
                return Results.Conflict(new { outcome = ex.Message, current = coordinator.Snapshot() });
            }
        });

        endpoints.MapPost("/api/v1/internal/option-chain/operations/recover", async (
            HttpRequest request,
            OptionChainOperatorCommand command,
            OptionChainOperationsOptions options,
            OptionChainDurableRolloutCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            if (!OptionChainOperatorAuthorization.IsAuthorized(request, options)) return Results.Unauthorized();
            try
            {
                var result = await coordinator.ApplyAsync(command, false, cancellationToken);
                return Results.Ok(new { outcome = result.Replayed ? "REPLAYED" : "APPLIED", result.State });
            }
            catch (InvalidOperationException ex) when (ex.Message == "STALE_ROLLOUT_VERSION")
            {
                return Results.Conflict(new { outcome = ex.Message, current = coordinator.Snapshot() });
            }
        });

        endpoints.MapDelete("/api/v1/internal/option-chain/operations/audit/retention", async (
            HttpRequest request,
            OptionChainOperationsOptions options,
            IOptionChainRolloutAuditStore store,
            CancellationToken cancellationToken) =>
        {
            if (!OptionChainOperatorAuthorization.IsAuthorized(request, options)) return Results.Unauthorized();
            var cutoff = DateTimeOffset.UtcNow.AddDays(-options.AuditRetentionDays);
            return Results.Ok(new { removed = await store.DeleteExpiredAsync(cutoff, cancellationToken), cutoffUtc = cutoff });
        });

        return endpoints;
    }
}

public static class OptionChainOperationsRegistration
{
    public static IServiceCollection AddOptionChainOperations(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("OptionChainOperations").Get<OptionChainOperationsOptions>() ?? new();
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<IOptionChainRolloutAuditStore, InMemoryOptionChainRolloutAuditStore>();
        services.AddSingleton<OptionChainDurableRolloutCoordinator>();
        services.AddSingleton<OptionChainScheduleLease>();
        return services;
    }
}
