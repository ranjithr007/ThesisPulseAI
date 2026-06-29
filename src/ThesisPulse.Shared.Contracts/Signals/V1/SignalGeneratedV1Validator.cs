namespace ThesisPulse.Shared.Contracts.Signals.V1;

public static class SignalGeneratedV1Validator
{
    public static IReadOnlyCollection<string> Validate(SignalGeneratedV1 signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var errors = new List<string>();

        if (signal.SignalUid == Guid.Empty)
        {
            errors.Add("signalUid must not be empty");
        }

        RequireText(signal.InstrumentKey, "instrumentKey", errors);
        RequireText(signal.StrategyCode, "strategyCode", errors);
        RequireText(signal.StrategyVersion, "strategyVersion", errors);
        RequireText(signal.InvalidationReason, "invalidationReason", errors);

        if (!SignalContractV1.Directions.Contains(signal.Direction))
        {
            errors.Add("direction is not supported");
        }

        if (!SignalContractV1.Timeframes.Contains(signal.PrimaryTimeframe))
        {
            errors.Add("primaryTimeframe is not supported");
        }

        foreach (var timeframe in signal.ConfirmationTimeframes)
        {
            if (!SignalContractV1.Timeframes.Contains(timeframe))
            {
                errors.Add($"confirmation timeframe '{timeframe}' is not supported");
            }
        }

        if (signal.Strength is < 0 or > 1)
        {
            errors.Add("strength must be between 0 and 1");
        }

        if (signal.Confidence is < 0 or > 1)
        {
            errors.Add("confidence must be between 0 and 1");
        }

        if (signal.ExpectedHoldingPeriodMinutes < 1)
        {
            errors.Add("expectedHoldingPeriodMinutes must be at least 1");
        }

        if (signal.EntryClosesAtUtc <= signal.EntryOpensAtUtc)
        {
            errors.Add("entryClosesAtUtc must be later than entryOpensAtUtc");
        }

        if (signal.ValidUntilUtc <= signal.GeneratedAtUtc)
        {
            errors.Add("validUntilUtc must be later than generatedAtUtc");
        }

        if (signal.EntryClosesAtUtc > signal.ValidUntilUtc)
        {
            errors.Add("entryClosesAtUtc must not be later than validUntilUtc");
        }

        ValidatePrices(signal, errors);
        ValidateEvidence(signal, errors);
        return errors;
    }

    private static void ValidatePrices(
        SignalGeneratedV1 signal,
        ICollection<string> errors)
    {
        if (signal.ReferencePrice <= 0)
        {
            errors.Add("referencePrice must be greater than zero");
        }

        if (signal.InvalidationPrice <= 0)
        {
            errors.Add("invalidationPrice must be greater than zero");
        }

        if (signal.MinimumPrice is <= 0)
        {
            errors.Add("minimumPrice must be greater than zero when provided");
        }

        if (signal.MaximumPrice is <= 0)
        {
            errors.Add("maximumPrice must be greater than zero when provided");
        }

        if (signal.MinimumPrice.HasValue &&
            signal.ReferencePrice < signal.MinimumPrice.Value)
        {
            errors.Add("referencePrice must not be below minimumPrice");
        }

        if (signal.MaximumPrice.HasValue &&
            signal.ReferencePrice > signal.MaximumPrice.Value)
        {
            errors.Add("referencePrice must not be above maximumPrice");
        }

        if (signal.MinimumPrice.HasValue &&
            signal.MaximumPrice.HasValue &&
            signal.MaximumPrice.Value < signal.MinimumPrice.Value)
        {
            errors.Add("maximumPrice must not be below minimumPrice");
        }
    }

    private static void ValidateEvidence(
        SignalGeneratedV1 signal,
        ICollection<string> errors)
    {
        foreach (var evidence in signal.Evidence)
        {
            RequireText(evidence.Code, "evidence.code", errors);
            RequireText(evidence.Message, "evidence.message", errors);

            if (evidence.Weight is < 0 or > 1)
            {
                errors.Add("evidence weight must be between 0 and 1 when provided");
            }
        }
    }

    private static void RequireText(
        string? value,
        string fieldName,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{fieldName} is required");
        }
    }
}
