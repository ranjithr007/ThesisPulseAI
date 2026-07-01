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
    ISignalStore signalStore,
    IFusionSignalStore fusionSignalStore)
{
    private const string ConsumerName =
        "ThesisPulse.Signal.Service.SignalGeneratedV1";
    private const string FusionConsumerName =
        "ThesisPulse.Signal.Service.FusionSignalIntakeV1";

    public Task<SignalIntakeResult> ProcessAsync(
        EventEnvelope<SignalGeneratedV1> envelope,
        CancellationToken cancellationToken = default) =>
        ProcessStandardAsync(envelope, cancellationToken);

    public async Task<SignalIntakeResult> ProcessFusionAsync(
        FusionSignalIntakeV1 intake,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intake);
        var envelope = intake.Envelope;
        var errors = Validate(envelope)
            .Concat(ValidateFusionLineage(intake))
            .ToArray();
        if (errors.Length > 0)
        {
            return Invalid(envelope, errors);
        }

        SignalSaveResult saveResult;
        try
        {
            saveResult = await fusionSignalStore.SaveFusionAsync(intake, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(envelope, exception.Message);
        }

        var inboxEnvelope = new EventEnvelope<FusionSignalIntakeV1>(
            envelope.Metadata,
            intake);
        var processingResult = await processor.ProcessAsync(
            inboxEnvelope,
            FusionConsumerName,
            static (_, _) => Task.CompletedTask,
            cancellationToken);
        if (processingResult.Outcome == InboxProcessingOutcome.Failed)
        {
            return Build(
                SignalIntakeOutcome.Failed,
                envelope,
                saveResult,
                processingResult.Error);
        }
        if (processingResult.Outcome == InboxProcessingOutcome.Duplicate)
        {
            return Build(
                SignalIntakeOutcome.DuplicateMessage,
                envelope,
                saveResult,
                Error: null);
        }

        return Build(
            saveResult.Outcome == SignalSaveOutcome.Duplicate
                ? SignalIntakeOutcome.DuplicateSignal
                : SignalIntakeOutcome.Accepted,
            envelope,
            saveResult,
            Error: null);
    }

    private async Task<SignalIntakeResult> ProcessStandardAsync(
        EventEnvelope<SignalGeneratedV1> envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var errors = Validate(envelope).ToArray();
        if (errors.Length > 0)
        {
            return Invalid(envelope, errors);
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

    private static IEnumerable<string> Validate(
        EventEnvelope<SignalGeneratedV1> envelope) =>
        SignalMetadataValidator.Validate(envelope.Metadata)
            .Concat(SignalGeneratedV1Validator.Validate(envelope.Payload))
            .Concat(SignalPersistenceValidator.Validate(envelope.Payload));

    private static IEnumerable<string> ValidateFusionLineage(FusionSignalIntakeV1 intake)
    {
        var lineage = intake.Lineage;
        var envelope = intake.Envelope;
        if (lineage.ThesisUid == Guid.Empty)
            yield return "lineage.thesisUid must not be empty";
        if (lineage.ThesisRequestUid == Guid.Empty)
            yield return "lineage.thesisRequestUid must not be empty";
        if (lineage.CandidateSignalUid != envelope.Payload.SignalUid)
            yield return "lineage.candidateSignalUid must match payload.signalUid";
        if (lineage.FusionEvidenceUid == Guid.Empty)
            yield return "lineage.fusionEvidenceUid must not be empty";
        if (lineage.SourceCandleMessageUid == Guid.Empty)
            yield return "lineage.sourceCandleMessageUid must not be empty";
        if (lineage.ConfirmationOutputUid == Guid.Empty)
            yield return "lineage.confirmationOutputUid must not be empty";
        if (lineage.ConfirmationMessageUid == Guid.Empty)
            yield return "lineage.confirmationMessageUid must not be empty";
        if (!string.Equals(
                envelope.Metadata.CausationId,
                lineage.FusionEvidenceUid.ToString("D"),
                StringComparison.OrdinalIgnoreCase))
            yield return "metadata.causationId must match lineage.fusionEvidenceUid";
        if (string.IsNullOrWhiteSpace(lineage.FusionEngineVersion))
            yield return "lineage.fusionEngineVersion is required";
        if (string.IsNullOrWhiteSpace(lineage.FusionPolicyVersion))
            yield return "lineage.fusionPolicyVersion is required";
        if (string.IsNullOrWhiteSpace(lineage.WeightConfigurationVersion))
            yield return "lineage.weightConfigurationVersion is required";
    }

    private static SignalIntakeResult Invalid(
        EventEnvelope<SignalGeneratedV1> envelope,
        IReadOnlyCollection<string> errors) =>
        new(
            SignalIntakeOutcome.Invalid,
            envelope.Metadata.MessageId,
            envelope.Payload.SignalUid,
            null,
            errors,
            null);

    private static SignalIntakeResult Failed(
        EventEnvelope<SignalGeneratedV1> envelope,
        string error) =>
        new(
            SignalIntakeOutcome.Failed,
            envelope.Metadata.MessageId,
            envelope.Payload.SignalUid,
            null,
            Array.Empty<string>(),
            error);

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
}
