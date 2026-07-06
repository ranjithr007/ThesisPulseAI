namespace ThesisPulse.Shared.Observability.Auditing;

public sealed class OperatorAccessAuditOptions
{
    public const string SectionName = "OperatorAccessAudit";

    public int Capacity { get; set; } = 500;

    public int MaximumReadLimit { get; set; } = 200;

    public void Validate()
    {
        if (Capacity is < 10 or > 10_000)
        {
            throw new InvalidOperationException(
                "OperatorAccessAudit:Capacity must be between 10 and 10000.");
        }

        if (MaximumReadLimit is < 1 or > 1_000)
        {
            throw new InvalidOperationException(
                "OperatorAccessAudit:MaximumReadLimit must be between 1 and 1000.");
        }

        if (MaximumReadLimit > Capacity)
        {
            throw new InvalidOperationException(
                "OperatorAccessAudit:MaximumReadLimit cannot exceed OperatorAccessAudit:Capacity.");
        }
    }
}
