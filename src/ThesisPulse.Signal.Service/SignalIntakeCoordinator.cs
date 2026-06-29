using ThesisPulse.Shared.Contracts.Messaging.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Infrastructure.Messaging;
using ThesisPulse.Shared.Infrastructure.Signals;

namespace ThesisPulse.Signal.Service;

public enum SignalIntakeOutcome
{
    Accepted = 0,
    DuplicateMessage = 1,
    DuplicateSignal = 2,
    Invalid = 3,
    Failed = 4,
}

public sealed record SignalIntakeResult(
    SignalIntakeOutcome Outcome,
    Guid MessageId,
    Guid SignalUid,
    long? SignalId,
    IReadOnlyCollection<string> Errors,
    string? Error);

public sealed class SignalIntakeCoordinator(
    InboxMessageProcessor processor,
    ISignalStore signalStore)
{
    private const string ConsumerName =
        "ThesisPulse.Signal.Service.SignalGeneratedV1";

    public async Task<SignalIntakeResult> ProcessAsync(
        EventEnvelope<SignalGeneratedV1> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var errors = ValidateMetadata(envelope.Metadata)
            .Concat(SignalGeneratedV1Validator.Validate(envelope.Payload))
            .ToArray();

        if (errors.Length > 0)
        {
            return new SignalIntakeResult(
                SignalIntakeOutcome.Invalid,
                envelope.Metadata.MessageId,
                envelope.Payload.SignalUid,
                SignalId: null,
                errors,
                Error: null);
        }

        SignalSaveResult? saveResult = null;
        var processingResult = await processor.ProcessAsync(
            envelope,
            ConsumerName,
            async (_, token) =>
            {
                saveResult = await signalStore.SaveAsync(envelope, token);
            },
            cancellationToken);

        if (processingResult.Outcome == InboxProcessingOutcome.Duplicate)
        {
            return Build(
                SignalIntakeOutcome.DuplicateMessage,
                envelope,
                saveResult,
                Error: null);
        }

        if (processingResult.Outcome == InboxProcessingOutcome.Failed)
        {
            return Build(
                SignalIntakeOutcome.Failed,
                envelope,
                saveResult,
                processingResult.Error);
        }

        return Build(
            saveResult?.Outcome == SignalSaveOutcome.Duplicate
                ? SignalIntakeOutcome.DuplicateSignal
                : SignalIntakeOutcome.Accepted,
            envelope,
            saveResult,
            Error: null);
    }

    private static SignalIntakeResult Build(
        SignalIntakeOutcome outcome,
        EventEnvelope<SignalGeneratedV1> envelope,
        SignalSaveResult? saveResult,
        string? Error) =>
        new(
            outcome,
            envelope.Metadata.MessageId,
            saveResult?.SignalUid ?? envelope.Payload.SignalUid,
            saveResult?.SignalId,
            Array.Empty<string>(),
            Error);

    private static IReadOnlyCollection<string> ValidateMetadata(
        MessageMetadata metadata)
    {
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
