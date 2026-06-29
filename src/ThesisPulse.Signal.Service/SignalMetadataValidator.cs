using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Signal.Service;

public static class SignalMetadataValidator
{
    public static IReadOnlyCollection<string> Validate(MessageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var errors = new List<string>();

        if (metadata.MessageId == Guid.Empty)
        {
            errors.Add("metadata.messageId must not be empty");
        }

        if (!metadata.EventType.Equals(
                SignalContractV1.EventType,
                StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"metadata.eventType must be {SignalContractV1.EventType}");
        }

        if (!metadata.ContractVersion.Equals(
                SignalContractV1.ContractVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"metadata.contractVersion must be {SignalContractV1.ContractVersion}");
        }

        if (!metadata.Environment.Equals("PAPER", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Phase 1 signal intake accepts PAPER messages only");
        }

        AddRequired(metadata.CorrelationId, "metadata.correlationId", errors);
        AddRequired(metadata.Producer, "metadata.producer", errors);
        AddRequired(metadata.ProducerVersion, "metadata.producerVersion", errors);
        AddRequired(
            metadata.ConfigurationVersion,
            "metadata.configurationVersion",
            errors);

        return errors;
    }

    private static void AddRequired(
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
