using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.Risk.Service;

public sealed record SignalRiskCoordinatorResult(
    string Outcome,
    StoredRiskEvaluation Evaluation);

public sealed class SignalRiskCoordinator(
    IRiskDecisionEngine engine,
    ISignalRiskEvaluationStore store)
{
    private readonly object _sync = new();

    public SignalRiskCoordinatorResult Evaluate(SignalRiskEvaluationCommandV1 command)
    {
        lock (_sync)
        {
            var existing = store.Get(command.CommandUid);
            if (existing is not null)
                return new SignalRiskCoordinatorResult("DUPLICATE", existing);

            RiskStatusTransitionMatrix.EnsureAllowed(
                RiskStatusTransitionMatrix.NotEvaluated,
                SignalRiskEvaluationContractV1.RiskEvaluating);

            var decision = engine.Evaluate(command.Request);
            var finalStatus = decision.Decision == RiskDecisionContractV1.Approved
                ? SignalRiskEvaluationContractV1.RiskApproved
                : SignalRiskEvaluationContractV1.RiskRejected;

            RiskStatusTransitionMatrix.EnsureAllowed(
                SignalRiskEvaluationContractV1.RiskEvaluating,
                finalStatus);

            var evaluation = new StoredRiskEvaluation(
                command.CommandUid,
                command.SignalUid,
                decision,
                finalStatus,
                new[]
                {
                    RiskStatusTransitionMatrix.NotEvaluated,
                    SignalRiskEvaluationContractV1.RiskEvaluating,
                    finalStatus,
                });

            return new SignalRiskCoordinatorResult("COMPLETED", store.Save(evaluation));
        }
    }
}
