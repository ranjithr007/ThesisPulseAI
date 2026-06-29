using ThesisPulse.Shared.Contracts.Signals.V1;

namespace ThesisPulse.Shared.Infrastructure.Signals;

public static class SignalStatusTransitionRules
{
    public static string? Validate(
        string currentStatus,
        SignalStatusTransitionV1 transition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentStatus);
        ArgumentNullException.ThrowIfNull(transition);

        if (transition.TransitionUid == Guid.Empty)
        {
            return "transitionUid must not be empty";
        }

        if (!SignalStatusV1.Values.Contains(transition.TargetStatus))
        {
            return $"Unsupported target status '{transition.TargetStatus}'.";
        }

        if (string.IsNullOrWhiteSpace(transition.SourceService) ||
            string.IsNullOrWhiteSpace(transition.SourceVersion) ||
            string.IsNullOrWhiteSpace(transition.CorrelationId))
        {
            return "sourceService, sourceVersion, and correlationId are required";
        }

        if (transition.TargetStatus.Equals(
                SignalStatusV1.Rejected,
                StringComparison.OrdinalIgnoreCase) &&
            transition.ReasonCodes.Count == 0)
        {
            return "REJECTED requires at least one reason code";
        }

        if (transition.TargetStatus.Equals(
                SignalStatusV1.Superseded,
                StringComparison.OrdinalIgnoreCase) &&
            transition.RelatedSignalUid is null)
        {
            return "SUPERSEDED requires relatedSignalUid";
        }

        return IsAllowed(currentStatus, transition.TargetStatus)
            ? null
            : $"Transition from {currentStatus} to {transition.TargetStatus} is not allowed.";
    }

    public static bool IsAllowed(string currentStatus, string targetStatus)
    {
        if (currentStatus.Equals(SignalStatusV1.Candidate, StringComparison.OrdinalIgnoreCase))
        {
            return targetStatus.Equals(SignalStatusV1.Validated, StringComparison.OrdinalIgnoreCase)
                || targetStatus.Equals(SignalStatusV1.Rejected, StringComparison.OrdinalIgnoreCase)
                || targetStatus.Equals(SignalStatusV1.Expired, StringComparison.OrdinalIgnoreCase)
                || targetStatus.Equals(SignalStatusV1.Superseded, StringComparison.OrdinalIgnoreCase);
        }

        if (currentStatus.Equals(SignalStatusV1.Validated, StringComparison.OrdinalIgnoreCase))
        {
            return targetStatus.Equals(SignalStatusV1.Expired, StringComparison.OrdinalIgnoreCase)
                || targetStatus.Equals(SignalStatusV1.Superseded, StringComparison.OrdinalIgnoreCase)
                || targetStatus.Equals(SignalStatusV1.Consumed, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
