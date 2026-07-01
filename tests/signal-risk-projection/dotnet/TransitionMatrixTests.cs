using ThesisPulse.Risk.Service;
using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.SignalRiskProjection.Tests;

public static class TransitionMatrixTests
{
    public static void RunAll()
    {
        AssertAllowed(RiskStatusTransitionMatrix.NotEvaluated, SignalRiskEvaluationContractV1.RiskEvaluating);
        AssertAllowed(SignalRiskEvaluationContractV1.RiskEvaluating, SignalRiskEvaluationContractV1.RiskApproved);
        AssertAllowed(SignalRiskEvaluationContractV1.RiskEvaluating, SignalRiskEvaluationContractV1.RiskRejected);
        AssertAllowed(SignalRiskEvaluationContractV1.RiskEvaluating, SignalRiskEvaluationContractV1.RiskRestricted);
        AssertAllowed(SignalRiskEvaluationContractV1.RiskEvaluating, SignalRiskEvaluationContractV1.RiskRetryPending);
        AssertBlocked(SignalRiskEvaluationContractV1.RiskApproved, SignalRiskEvaluationContractV1.RiskEvaluating);
        AssertBlocked(SignalRiskEvaluationContractV1.RiskRejected, SignalRiskEvaluationContractV1.RiskApproved);
    }

    private static void AssertAllowed(string fromStatus, string toStatus)
    {
        if (!RiskStatusTransitionMatrix.CanTransition(fromStatus, toStatus))
            throw new InvalidOperationException($"Expected transition {fromStatus} to {toStatus} to be allowed.");
    }

    private static void AssertBlocked(string fromStatus, string toStatus)
    {
        if (RiskStatusTransitionMatrix.CanTransition(fromStatus, toStatus))
            throw new InvalidOperationException($"Expected transition {fromStatus} to {toStatus} to be blocked.");
    }
}
