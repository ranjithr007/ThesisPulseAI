using System.Collections.Concurrent;
using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.Risk.Service;

public sealed record StoredRiskEvaluation(
    Guid CommandUid,
    Guid SignalUid,
    RiskDecisionV1 Decision,
    string CurrentStatus,
    IReadOnlyCollection<string> StatusHistory);

public interface ISignalRiskEvaluationStore
{
    StoredRiskEvaluation? Get(Guid commandUid);
    StoredRiskEvaluation Save(
        SignalRiskEvaluationCommandV1 command,
        StoredRiskEvaluation evaluation);
}

public sealed class InMemorySignalRiskEvaluationStore : ISignalRiskEvaluationStore
{
    private readonly ConcurrentDictionary<Guid, StoredRiskEvaluation> _items = new();

    public StoredRiskEvaluation? Get(Guid commandUid) =>
        _items.TryGetValue(commandUid, out var value) ? value : null;

    public StoredRiskEvaluation Save(
        SignalRiskEvaluationCommandV1 command,
        StoredRiskEvaluation evaluation) =>
        _items.GetOrAdd(command.CommandUid, evaluation);
}
