using System.Diagnostics.Metrics;

namespace ThesisPulse.Signal.Service;

public sealed class OptionChainOperationsMetrics : IDisposable
{
    private readonly Meter _meter = new("ThesisPulse.Signal.Service.OptionChainOperations");
    private readonly Counter<long> _events;

    public OptionChainOperationsMetrics()
    {
        _events = _meter.CreateCounter<long>("thesispulse_option_chain_operations_events");
    }

    public void Record(string eventName, string outcome)
    {
        _events.Add(1,
            new KeyValuePair<string, object?>("event", eventName),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    public void Dispose() => _meter.Dispose();
}
