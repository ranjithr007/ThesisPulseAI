using System.Collections.Concurrent;
using ThesisPulse.Shared.Contracts.Workflows.V1;

namespace ThesisPulse.Operations.Service;

public sealed record StoredPaperWorkflowStep(
    PaperWorkflowStepSnapshotV1 Snapshot,
    string? RequestJson,
    string? ResponseJson);

public sealed record StoredPaperWorkflow(
    PaperWorkflowSnapshotV1 Snapshot,
    PaperWorkflowStartRequestV1 Request,
    string? ResultJson,
    IReadOnlyDictionary<string, StoredPaperWorkflowStep> Steps);

public interface IPaperWorkflowStore
{
    Task<StoredPaperWorkflow?> FindByIdempotencyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<StoredPaperWorkflow?> GetAsync(
        Guid workflowUid,
        CancellationToken cancellationToken = default);

    Task<StoredPaperWorkflow> CreateAsync(
        StoredPaperWorkflow workflow,
        CancellationToken cancellationToken = default);

    Task<StoredPaperWorkflow> SaveAsync(
        StoredPaperWorkflow workflow,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Guid>> GetDueWorkflowUidsAsync(
        DateTimeOffset asOfUtc,
        int maximumCount,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryPaperWorkflowStore : IPaperWorkflowStore
{
    private readonly ConcurrentDictionary<Guid, StoredPaperWorkflow> _byUid = new();
    private readonly ConcurrentDictionary<string, Guid> _byIdempotency =
        new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public Task<StoredPaperWorkflow?> FindByIdempotencyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_byIdempotency.TryGetValue(idempotencyKey, out var workflowUid) ||
                !_byUid.TryGetValue(workflowUid, out var workflow))
            {
                return Task.FromResult<StoredPaperWorkflow?>(null);
            }

            return Task.FromResult<StoredPaperWorkflow?>(Clone(workflow));
        }
    }

    public Task<StoredPaperWorkflow?> GetAsync(
        Guid workflowUid,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(
                _byUid.TryGetValue(workflowUid, out var workflow)
                    ? Clone(workflow)
                    : null);
        }
    }

    public Task<StoredPaperWorkflow> CreateAsync(
        StoredPaperWorkflow workflow,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_byIdempotency.TryGetValue(
                    workflow.Snapshot.IdempotencyKey,
                    out var existingUid))
            {
                return Task.FromResult(Clone(_byUid[existingUid]));
            }

            if (!_byUid.TryAdd(workflow.Snapshot.WorkflowUid, Clone(workflow)))
            {
                return Task.FromResult(Clone(_byUid[workflow.Snapshot.WorkflowUid]));
            }

            _byIdempotency[workflow.Snapshot.IdempotencyKey] =
                workflow.Snapshot.WorkflowUid;
            return Task.FromResult(Clone(workflow));
        }
    }

    public Task<StoredPaperWorkflow> SaveAsync(
        StoredPaperWorkflow workflow,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_byUid.ContainsKey(workflow.Snapshot.WorkflowUid))
            {
                throw new InvalidOperationException("Workflow does not exist.");
            }

            _byUid[workflow.Snapshot.WorkflowUid] = Clone(workflow);
            return Task.FromResult(Clone(workflow));
        }
    }

    public Task<IReadOnlyCollection<Guid>> GetDueWorkflowUidsAsync(
        DateTimeOffset asOfUtc,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        lock (_sync)
        {
            IReadOnlyCollection<Guid> result = _byUid.Values
                .Where(workflow =>
                    workflow.Snapshot.Status == PaperWorkflowContractV1.RetryPending &&
                    workflow.Snapshot.NextAttemptAtUtc <= asOfUtc)
                .OrderBy(workflow => workflow.Snapshot.NextAttemptAtUtc)
                .Take(maximumCount)
                .Select(workflow => workflow.Snapshot.WorkflowUid)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    private static StoredPaperWorkflow Clone(StoredPaperWorkflow workflow)
    {
        var steps = workflow.Steps.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with { Snapshot = pair.Value.Snapshot with { } },
            StringComparer.Ordinal);
        return workflow with
        {
            Snapshot = workflow.Snapshot with
            {
                Steps = steps.Values
                    .OrderBy(step => step.Snapshot.Sequence)
                    .Select(step => step.Snapshot)
                    .ToArray(),
            },
            Steps = steps,
        };
    }
}
