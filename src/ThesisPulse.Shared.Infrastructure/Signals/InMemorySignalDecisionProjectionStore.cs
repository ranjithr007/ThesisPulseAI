using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public sealed class InMemorySignalDecisionProjectionStore(
    InMemorySignalStore signals) : ISignalDecisionProjectionStore
{
    public async Task<SignalDecisionProjectionV1?> GetDecisionProjectionAsync(
        Guid signalUid,
        CancellationToken cancellationToken = default)
    {
        var signal = await signals.GetAsync(signalUid, cancellationToken);
        return signal is null
            ? null
            : new SignalDecisionProjectionV1(
                signalUid,
                SignalScannerContractV1.RiskNotEvaluated,
                null,
                null,
                new SignalTradePlanProjectionV1(
                    SignalScannerContractV1.PlanNotAvailable,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false));
    }
}
