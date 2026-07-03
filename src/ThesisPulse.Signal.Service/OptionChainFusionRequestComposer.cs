using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Signal.Service;

public sealed record OptionChainFusionRequestComposition(
    ThesisFusionRequestV1 Request,
    OptionChainFusionEvidenceResult OptionChain,
    bool EvidenceAdded);

public sealed class OptionChainFusionRequestComposer(
    IOptionChainFusionEvidenceProvider optionChainProvider)
{
    public async Task<OptionChainFusionRequestComposition> ComposeAsync(
        ThesisFusionRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var optionChain = await optionChainProvider.BuildAsync(
            request.InstrumentKey,
            request.AsOfUtc,
            cancellationToken);

        var existing = request.DirectionalEvidence
            .Where(item => !string.Equals(
                item.EngineCode,
                "OPTION_CHAIN",
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        var evidenceAdded = optionChain.Evidence is not null;
        if (optionChain.Evidence is not null)
            existing.Add(optionChain.Evidence);

        var composed = request with
        {
            DirectionalEvidence = existing.ToArray(),
        };

        return new OptionChainFusionRequestComposition(
            composed,
            optionChain,
            evidenceAdded);
    }
}
