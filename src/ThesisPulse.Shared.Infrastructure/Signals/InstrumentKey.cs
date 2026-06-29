namespace ThesisPulse.Shared.Infrastructure.Signals;

internal sealed record InstrumentKey(string ExchangeCode, string LookupValue)
{
    public static InstrumentKey Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var parts = value.Split('|', 2, StringSplitOptions.TrimEntries);

        if (parts.Length != 2)
        {
            throw new ArgumentException("Instrument key format is invalid.", nameof(value));
        }

        var exchangeToken = parts[0];
        var separatorIndex = exchangeToken.IndexOf('_');
        var exchangeCode = separatorIndex > 0
            ? exchangeToken[..separatorIndex]
            : exchangeToken;

        if (string.IsNullOrWhiteSpace(exchangeCode) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new ArgumentException("Instrument key values are required.", nameof(value));
        }

        return new InstrumentKey(
            exchangeCode.Trim().ToUpperInvariant(),
            parts[1].Trim());
    }
}
