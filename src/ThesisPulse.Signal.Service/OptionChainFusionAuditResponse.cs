using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Signal.Service;

public sealed record OptionChainFusionAuditResponse(
    string RequestUid,
    string InstrumentKey,
    DateTimeOffset CutoffUtc,
    string Outcome,
    Guid? OutputUid,
    int? Revision,
    IReadOnlyCollection<string> Warnings,
    IReadOnlyCollection<string> GateFailures,
    ThesisFusionRequestV1? Request,
    bool CanContinue);
