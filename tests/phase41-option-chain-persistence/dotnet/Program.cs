using ThesisPulse.Shared.Contracts.Intelligence.V1;
using ThesisPulse.Signal.Service;

var tests = new (string Name, Func<Task> Run)[]
{
    ("duplicate replay is idempotent", DuplicateReplayIsIdempotent),
    ("payload conflict fails closed", PayloadConflictFailsClosed),
    ("revision must advance", RevisionMustAdvance),
    ("future knowledge is excluded", FutureKnowledgeIsExcluded),
    ("latest eligible revision is deterministic", LatestEligibleRevisionIsDeterministic),
    ("authority drift is rejected", AuthorityDriftIsRejected),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL: {test.Name}: {exception.Message}");
    }
}

return failed == 0 ? 0 : 1;

static async Task DuplicateReplayIsIdempotent()
{
    var store = new InMemoryOptionChainIntelligenceOutputStore();
    var envelope = Envelope(Output(Guid.Parse("10000000-0000-0000-0000-000000000001"), 0));

    Equal(OptionChainAppendOutcome.Inserted, (await store.AppendAsync(envelope)).Outcome, "first append");
    Equal(OptionChainAppendOutcome.Duplicate, (await store.AppendAsync(envelope)).Outcome, "duplicate append");
}

static async Task PayloadConflictFailsClosed()
{
    var store = new InMemoryOptionChainIntelligenceOutputStore();
    var uid = Guid.Parse("20000000-0000-0000-0000-000000000001");
    var first = Envelope(Output(uid, 0));
    var conflicting = first with { PersistedAtUtc = first.PersistedAtUtc.AddSeconds(1) };

    await store.AppendAsync(first);
    var result = await store.AppendAsync(conflicting);

    Equal(OptionChainAppendOutcome.Rejected, result.Outcome, "conflict outcome");
    Equal("OUTPUT_UID_PAYLOAD_CONFLICT", result.Reason, "conflict reason");
}

static async Task RevisionMustAdvance()
{
    var store = new InMemoryOptionChainIntelligenceOutputStore();
    var first = Envelope(Output(Guid.Parse("30000000-0000-0000-0000-000000000001"), 1));
    var older = Envelope(Output(Guid.Parse("30000000-0000-0000-0000-000000000002"), 0));

    await store.AppendAsync(first);
    var result = await store.AppendAsync(older);

    Equal(OptionChainAppendOutcome.Rejected, result.Outcome, "revision outcome");
    Equal("REVISION_NOT_NEWER", result.Reason, "revision reason");
}

static async Task FutureKnowledgeIsExcluded()
{
    var store = new InMemoryOptionChainIntelligenceOutputStore();
    var asOf = new DateTimeOffset(2026, 7, 2, 9, 30, 0, TimeSpan.Zero);
    var output = Output(Guid.Parse("40000000-0000-0000-0000-000000000001"), 0, asOf) with
    {
        GeneratedAtUtc = asOf.AddMinutes(10),
    };
    var envelope = Envelope(output) with
    {
        SourceReceivedAtUtc = asOf.AddMinutes(5),
        PersistedAtUtc = asOf.AddMinutes(11),
    };

    Equal(OptionChainAppendOutcome.Inserted, (await store.AppendAsync(envelope)).Outcome, "future row append");

    var result = await store.GetLatestAtOrBeforeAsync(new OptionChainPointInTimeQuery(
        output.UnderlyingInstrumentKey,
        asOf.AddMinutes(6)));

    True(result is null, "future-generated output must be excluded");
}

static async Task LatestEligibleRevisionIsDeterministic()
{
    var store = new InMemoryOptionChainIntelligenceOutputStore();
    var asOf = new DateTimeOffset(2026, 7, 2, 9, 30, 0, TimeSpan.Zero);
    var revision0 = Envelope(Output(Guid.Parse("50000000-0000-0000-0000-000000000001"), 0, asOf));
    var revision1 = Envelope(Output(Guid.Parse("50000000-0000-0000-0000-000000000002"), 1, asOf)) with
    {
        SourceReceivedAtUtc = asOf.AddSeconds(3),
        PersistedAtUtc = asOf.AddSeconds(5),
    };

    await store.AppendAsync(revision0);
    await store.AppendAsync(revision1);

    var result = await store.GetLatestAtOrBeforeAsync(new OptionChainPointInTimeQuery(
        revision0.Output.UnderlyingInstrumentKey,
        asOf.AddMinutes(1)));

    True(result is not null, "result required");
    Equal(1, result!.Output.Revision, "latest revision");
}

static async Task AuthorityDriftIsRejected()
{
    var store = new InMemoryOptionChainIntelligenceOutputStore();
    var output = Output(Guid.Parse("60000000-0000-0000-0000-000000000001"), 0) with
    {
        ExecutionAuthority = true,
    };

    var result = await store.AppendAsync(Envelope(output));

    Equal(OptionChainAppendOutcome.Rejected, result.Outcome, "authority outcome");
    Equal("AUTHORITY_DRIFT", result.Reason, "authority reason");
}

static OptionChainPersistenceEnvelope Envelope(OptionChainIntelligenceOutputV1 output) => new(
    output,
    output.AsOfUtc.AddSeconds(2),
    output.GeneratedAtUtc.AddSeconds(2));

static OptionChainIntelligenceOutputV1 Output(
    Guid outputUid,
    int revision,
    DateTimeOffset? asOfUtc = null)
{
    var asOf = asOfUtc ?? new DateTimeOffset(2026, 7, 2, 9, 30, 0, TimeSpan.Zero);
    return new OptionChainIntelligenceOutputV1(
        outputUid,
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        new[] { Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb") },
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
        revision,
        Array.Empty<OptionChainEvidenceV1>(),
        Array.Empty<string>(),
        false,
        false);
}

static void True(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException(name);
}

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
}
