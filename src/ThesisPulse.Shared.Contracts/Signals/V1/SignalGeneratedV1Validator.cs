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

        if (string.IsNullOrWhiteSpace(signal.InstrumentKey))
        {
            errors.Add("instrumentKey is required");
        }

        if (!SignalContractV1.Directions.Contains(signal.Direction))
        {
            errors.Add("direction is not supported");
        }

        if (!SignalContractV1.Timeframes.Contains(signal.PrimaryTimeframe))
        {
            errors.Add("primaryTimeframe is not supported");
        }

        if (signal.Strength is < 0 or > 1)
        {
            errors.Add("strength must be between 0 and 1");
        }

        if (signal.Confidence is < 0 or > 1)
        {
            errors.Add("confidence must be between 0 and 1");
        }

        return errors;
    }
}
