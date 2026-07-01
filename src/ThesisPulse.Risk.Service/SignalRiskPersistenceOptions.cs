namespace ThesisPulse.Risk.Service;

public sealed class SignalRiskPersistenceOptions
{
    public const string SectionName = "SignalRiskPersistence";

    public string Mode { get; init; } = "IN_MEMORY";
    public string? ConnectionString { get; init; }

    public bool UseSqlServer => string.Equals(Mode, "SQL_SERVER", StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        if (UseSqlServer && string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("SignalRiskPersistence:ConnectionString is required in SQL_SERVER mode.");
    }
}
