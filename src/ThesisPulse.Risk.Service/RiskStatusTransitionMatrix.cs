using ThesisPulse.Shared.Contracts.Risk.V1;

namespace ThesisPulse.Risk.Service;

public static class RiskStatusTransitionMatrix
{
    public const string NotEvaluated = "NOT_EVALUATED";

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Allowed =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [NotEvaluated] = Set(SignalRiskEvaluationContractV1.RiskEvaluating, SignalRiskEvaluationContractV1.RiskExpired),
            [SignalRiskEvaluationContractV1.RiskRetryPending] = Set(SignalRiskEvaluationContractV1.RiskEvaluating, SignalRiskEvaluationContractV1.RiskExpired),
            [SignalRiskEvaluationContractV1.RiskEvaluating] = Set(
                SignalRiskEvaluationContractV1.RiskApproved,
                SignalRiskEvaluationContractV1.RiskRejected,
                SignalRiskEvaluationContractV1.RiskRestricted,
                SignalRiskEvaluationContractV1.RiskRetryPending,
                SignalRiskEvaluationContractV1.RiskExpired),
            [SignalRiskEvaluationContractV1.RiskApproved] = Set(),
            [SignalRiskEvaluationContractV1.RiskRejected] = Set(),
            [SignalRiskEvaluationContractV1.RiskRestricted] = Set(),
            [SignalRiskEvaluationContractV1.RiskExpired] = Set(),
        };

    public static bool CanTransition(string fromStatus, string toStatus) =>
        Allowed.TryGetValue(fromStatus, out var targets) && targets.Contains(toStatus);

    public static void EnsureAllowed(string fromStatus, string toStatus)
    {
        if (!CanTransition(fromStatus, toStatus))
            throw new InvalidOperationException($"Illegal risk-status transition '{fromStatus}' -> '{toStatus}'.");
    }

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);
}
