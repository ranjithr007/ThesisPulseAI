using System.Reflection;
using ThesisPulse.Shared.Contracts.Intelligence.V1;
using ThesisPulse.Signal.Service;

var tests = new (string Name, Action Run)[]
{
    ("valid SQL options are accepted", ValidOptionsAreAccepted),
    ("invalid SQL options fail closed", InvalidOptionsFailClosed),
    ("adapter implements persistence boundary", AdapterImplementsPersistenceBoundary),
    ("duplicate snapshot lineage is rejected", DuplicateSnapshotLineageIsRejected),
    ("authority drift is rejected", AuthorityDriftIsRejected),
    ("invalid Fusion eligibility is rejected", InvalidFusionEligibilityIsRejected),
    ("SQL append is serializable and locked", SqlAppendIsSerializableAndLocked),
    ("point-in-time query enforces all cutoffs", PointInTimeQueryEnforcesAllCutoffs),
    ("initial revision guard matches schema", InitialRevisionGuardMatchesSchema),
    ("existing normalized schema is reused", ExistingNormalizedSchemaIsReused),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL: {test.Name}: {exception.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void ValidOptionsAreAccepted()
{
    var options = ValidOptions();
    options.Validate();
    _ = new SqlServerOptionChainIntelligenceOutputStore(options);
}

static void InvalidOptionsFailClosed()
{
    Throws<ArgumentException>(() => new SqlServerOptionChainIntelligenceOutputStoreOptions
    {
        ConnectionString = " ",
    }.Validate(), "blank connection string");

    Throws<ArgumentOutOfRangeException>(() => ValidOptions() with
    {
        Environment = "PRODUCTION",
    }.Validate(), "invalid environment");

    Throws<ArgumentOutOfRangeException>(() => ValidOptions() with
    {
        OutputTimeToLive = TimeSpan.Zero,
    }.Validate(), "invalid TTL");

    Throws<ArgumentOutOfRangeException>(() => ValidOptions() with
    {
        CommandTimeoutSeconds = 301,
    }.Validate(), "invalid timeout");
}

static void AdapterImplementsPersistenceBoundary()
{
    True(
        typeof(IOptionChainIntelligenceOutputStore).IsAssignableFrom(
            typeof(SqlServerOptionChainIntelligenceOutputStore)),
        "SQL adapter must implement IOptionChainIntelligenceOutputStore");
}

static void DuplicateSnapshotLineageIsRejected()
{
    var snapshot = Guid.Parse("11111111-1111-1111-1111-111111111111");
    var output = Output(sourceSnapshotUids: new[] { snapshot, snapshot });

    Equal(
        "SOURCE_SNAPSHOT_DUPLICATE",
        ValidateEnvelope(Envelope(output)),
        "duplicate source snapshot reason");
}

static void AuthorityDriftIsRejected()
{
    var output = Output() with { ExecutionAuthority = true };

    Equal("AUTHORITY_DRIFT", ValidateEnvelope(Envelope(output)), "authority reason");
}

static void InvalidFusionEligibilityIsRejected()
{
    var output = Output() with
    {
        DataQualityStatus = "INVALID",
        IsEligibleForFusion = true,
    };

    Equal(
        "FUSION_ELIGIBILITY_INVALID",
        ValidateEnvelope(Envelope(output)),
        "Fusion eligibility reason");
}

static void SqlAppendIsSerializableAndLocked()
{
    var source = AdapterSource();

    Contains(source, "IsolationLevel.Serializable", "serializable transaction");
    Contains(source, "WITH (UPDLOCK, HOLDLOCK)", "update and range locks");
    Contains(source, "OUTPUT_UID_PAYLOAD_CONFLICT", "conflicting replay guard");
    Contains(source, "SQL_UNIQUENESS_CONFLICT", "database uniqueness guard");
    Contains(source, "SHA256.HashData", "canonical SHA-256 contract hash");
}

static void PointInTimeQueryEnforcesAllCutoffs()
{
    var source = AdapterSource();

    Contains(source, "CROSS APPLY", "receipt-time correlation");
    Contains(source, "output.[as_of_utc] <= @cutoff_utc", "observation cutoff");
    Contains(source, "output.[generated_at_utc] <= @cutoff_utc", "generation cutoff");
    Contains(
        source,
        "receipt.[source_received_at_utc] <= @cutoff_utc",
        "source receipt cutoff");
    Contains(
        source,
        "ORDER BY output.[as_of_utc] DESC, output.[revision] DESC",
        "deterministic revision ordering");
    False(
        source.Contains(
            "GROUP BY output.[engine_output_id], output.[raw_contract_json]",
            StringComparison.Ordinal),
        "nvarchar(max) raw contract must not be grouped");
}

static void InitialRevisionGuardMatchesSchema()
{
    var source = AdapterSource();
    var migration = ReadRepositoryFile(
        "database/migrations/V0004__create_intelligence_and_signal_tables.sql");

    Contains(source, "INITIAL_REVISION_MUST_BE_ZERO", "adapter initial revision guard");
    Contains(
        migration,
        "([revision] = 0 AND [supersedes_engine_output_uid] IS NULL)",
        "schema initial revision constraint");
    Contains(
        migration,
        "([revision] > 0 AND [supersedes_engine_output_uid] IS NOT NULL)",
        "schema supersession constraint");
}

static void ExistingNormalizedSchemaIsReused()
{
    var source = AdapterSource();
    var migration = ReadRepositoryFile(
        "database/migrations/V0017__create_option_chain_intelligence_output_tables.sql");

    Contains(
        source,
        "[intelligence].[option_chain_output_snapshot_inputs]",
        "adapter lineage table");
    Contains(
        migration,
        "CREATE TABLE [intelligence].[option_chain_output_snapshot_inputs]",
        "existing lineage schema");
    Contains(
        migration,
        "CREATE TABLE [intelligence].[option_chain_output_expiries]",
        "existing normalized expiry schema");
    False(
        source.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase),
        "runtime adapter must not create schema");
}

static SqlServerOptionChainIntelligenceOutputStoreOptions ValidOptions() => new()
{
    ConnectionString = "Server=(local);Database=ThesisPulse;Integrated Security=true;TrustServerCertificate=true",
};

static OptionChainPersistenceEnvelope Envelope(OptionChainIntelligenceOutputV1 output) => new(
    output,
    output.AsOfUtc.AddSeconds(1),
    output.GeneratedAtUtc.AddSeconds(1));

static OptionChainIntelligenceOutputV1 Output(
    IReadOnlyCollection<Guid>? sourceSnapshotUids = null)
{
    var asOf = new DateTimeOffset(2026, 7, 3, 9, 30, 0, TimeSpan.Zero);
    return new OptionChainIntelligenceOutputV1(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        sourceSnapshotUids ?? new[]
        {
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
        },
        "NSE:NIFTY50",
        asOf,
        asOf.AddSeconds(1),
        OptionChainIntelligenceContractV1.EngineCode,
        OptionChainIntelligenceContractV1.EngineVersion,
        OptionChainIntelligenceContractV1.PolicyVersion,
        "LONG",
        0.70m,
        0.80m,
        Array.Empty<OptionChainExpiryMetricsV1>(),
        Array.Empty<OptionChainIvTermPointV1>(),
        null,
        null,
        "FLAT",
        1,
        0,
        0,
        1m,
        "VALID",
        false,
        true,
        0,
        Array.Empty<OptionChainEvidenceV1>(),
        Array.Empty<string>(),
        false,
        false);
}

static string? ValidateEnvelope(OptionChainPersistenceEnvelope envelope)
{
    var method = typeof(SqlServerOptionChainIntelligenceOutputStore).GetMethod(
        "ValidateEnvelope",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ValidateEnvelope method was not found.");

    return (string?)method.Invoke(null, new object[] { envelope });
}

static string AdapterSource() => ReadRepositoryFile(
    "src/ThesisPulse.Signal.Service/SqlServerOptionChainIntelligenceOutputStore.cs");

static string ReadRepositoryFile(string relativePath)
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null &&
           !File.Exists(Path.Combine(directory.FullName, "ThesisPulseAI.sln")))
    {
        directory = directory.Parent;
    }

    if (directory is null)
        throw new InvalidOperationException("Repository root could not be located.");

    var path = Path.Combine(
        directory.FullName,
        relativePath.Replace('/', Path.DirectorySeparatorChar));
    return File.ReadAllText(path);
}

static void Contains(string value, string expected, string name)
{
    if (!value.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"{name}: expected text was not found");
}

static void True(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException(name);
}

static void False(bool condition, string name) => True(!condition, name);

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
}

static void Throws<TException>(Action action, string name)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"{name}: expected {typeof(TException).Name}");
}
