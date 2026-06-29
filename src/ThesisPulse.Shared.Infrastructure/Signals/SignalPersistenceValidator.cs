using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public static class SignalPersistenceValidator
{
    private static readonly IReadOnlySet<string> EvidenceImpacts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SUPPORTS_LONG",
            "SUPPORTS_SHORT",
            "CONTRADICTS",
            "NEUTRAL",
        };

    public static IReadOnlyCollection<string> Validate(SignalGeneratedV1 signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var errors = new List<string>();
        var timeframes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var evidenceCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var timeframe in signal.ConfirmationTimeframes)
        {
            if (!timeframes.Add(timeframe))
            {
                errors.Add($"confirmation timeframe '{timeframe}' is duplicated");
            }
        }

        foreach (var evidence in signal.Evidence)
        {
            if (!EvidenceImpacts.Contains(evidence.Impact))
            {
                errors.Add($"evidence impact '{evidence.Impact}' is not supported");
            }

            if (!evidenceCodes.Add(evidence.Code))
            {
                errors.Add($"evidence code '{evidence.Code}' is duplicated");
            }
        }

        if (signal.Direction.Equals("LONG", StringComparison.OrdinalIgnoreCase) &&
            signal.InvalidationPrice >= signal.ReferencePrice)
        {
            errors.Add("LONG invalidationPrice must be below referencePrice");
        }

        if (signal.Direction.Equals("SHORT", StringComparison.OrdinalIgnoreCase) &&
            signal.InvalidationPrice <= signal.ReferencePrice)
        {
            errors.Add("SHORT invalidationPrice must be above referencePrice");
        }

        return errors;
    }
}
