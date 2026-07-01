using Microsoft.Extensions.Options;
using ThesisPulse.Risk.Service;
using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.SignalRiskProjection.Tests;

public static class CoordinatorSmokeTests
{
    public static void AssertApprovedAndReplaySafe(SignalRiskEvaluationCommandV1 command)
    {
        var coordinator = new SignalRiskCoordinator(
            new DeterministicRiskDecisionEngine(Options.Create(new DeterministicRiskOptions())),
            new InMemorySignalRiskEvaluationStore());

        var first = coordinator.Evaluate(command);
        var second = coordinator.Evaluate(command);

        if (first.Outcome != "COMPLETED")
            throw new InvalidOperationException("First evaluation must complete.");
        if (second.Outcome != "DUPLICATE")
            throw new InvalidOperationException("Replay must be reported as duplicate.");
        if (first.Evaluation.Decision.Decision != RiskDecisionContractV1.Approved)
            throw new InvalidOperationException("Healthy PAPER signal must be approved.");
        if (first.Evaluation.CurrentStatus != SignalRiskEvaluationContractV1.RiskApproved)
            throw new InvalidOperationException("Approved decision must end in RISK_APPROVED.");
        if (first.Evaluation.Decision.RiskDecisionUid != second.Evaluation.Decision.RiskDecisionUid)
            throw new InvalidOperationException("Replay must preserve the decision identity.");
        if (second.Evaluation.StatusHistory.Count != 3)
            throw new InvalidOperationException("Replay must not append duplicate status events.");
    }
}
