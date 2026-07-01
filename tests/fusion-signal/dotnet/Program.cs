using ThesisPulse.Shared.Contracts.Intelligence.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Infrastructure.Signals;
using ThesisPulse.Thesis.Service;

var tests = new (string Name, Func<Task> Run)[]
{
    ("eligible Fusion candidate projects deterministically", TestProjectionAsync),
    ("rejected thesis cannot project a signal", TestRejectedProjectionAsync),
    ("Fusion lineage is idempotent and scanner-ready", TestStoreAndScannerAsync),
    ("duplicate signal with changed lineage fails closed", TestLineageMismatchAsync),
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}

static Task TestProjectionAsync()
{
    var request = BuildRequest();
    var projector = new DeterministicFusionSignalProjector(new FusionSignalProjectorOptions());

    var first = projector.Project(request);
    var second = projector.Project(request);

    Assert(first.Outcome == FusionSignalProjectionContractV1.Projected, "projection should succeed");
    Assert(first.Intake is not null, "projection must return intake");
    Assert(second.Intake is not null, "repeat projection must return intake");
    Assert(first.Intake.Envelope.Metadata.MessageId == second.Intake.Envelope.Metadata.MessageId,
        "message identity must be deterministic");
    Assert(first.Intake.Envelope.Payload.SignalUid == request.Thesis.Candidate!.SignalUid,
        "candidate signal identity must be preserved");
    Assert(first.Intake.Envelope.Payload.Strength == 0.80m,
        "candidate strength must be normalized to the signal contract");
    Assert(first.Intake.Envelope.Payload.Confidence == 0.75m,
        "candidate confidence must be normalized to the signal contract");
    Assert(first.Intake.Lineage.FusionEvidenceUid == request.FusionEvidence.EvidenceUid,
        "Fusion evidence lineage must be exact");
    Assert(first.Intake.Envelope.Metadata.CausationId == request.FusionEvidence.EvidenceUid.ToString("D"),
        "causation must point to Fusion evidence");
    Assert(SignalGeneratedV1Validator.Validate(first.Intake.Envelope.Payload).Count == 0,
        "projected signal must satisfy the canonical signal contract");
    return Task.CompletedTask;
}

static Task TestRejectedProjectionAsync()
{
    var request = BuildRequest();
    var rejected = request.Thesis with
    {
        Decision = "REJECTED_BY_FUSION",
        Direction = EvidenceDirectionV1.Neutral,
        GateFailures = new[] { "DIRECTIONAL_CONFLICT" },
        Candidate = null,
    };
    var projector = new DeterministicFusionSignalProjector(new FusionSignalProjectorOptions());
    var result = projector.Project(request with { Thesis = rejected });

    Assert(result.Outcome == FusionSignalProjectionContractV1.Rejected,
        "rejected thesis must not project");
    Assert(result.Intake is null, "rejected thesis must not produce intake");
    Assert(result.Reasons.Contains("THESIS_CANDIDATE_REQUIRED"),
        "candidate gate must be explicit");
    return Task.CompletedTask;
}

static async Task TestStoreAndScannerAsync()
{
    var projector = new DeterministicFusionSignalProjector(new FusionSignalProjectorOptions());
    var projection = projector.Project(BuildRequest());
    var intake = projection.Intake ?? throw new InvalidOperationException("projection failed");
    var store = new InMemorySignalStore();

    var created = await store.SaveFusionAsync(intake);
    var duplicate = await store.SaveFusionAsync(intake);
    var active = await store.ScanAsync(
        new SignalScannerQueryV1(
            intake.Envelope.Payload.InstrumentKey,
            "LONG",
            SignalStatusV1.Candidate,
            0.70m,
            null,
            null,
            true,
            10),
        intake.Envelope.Payload.GeneratedAtUtc.AddMinutes(1));
    var expired = await store.ScanAsync(
        new SignalScannerQueryV1(null, null, null, null, null, null, true, 10),
        intake.Envelope.Payload.ValidUntilUtc.AddSeconds(1));

    Assert(created.Outcome == SignalSaveOutcome.Created, "first Fusion signal must be created");
    Assert(duplicate.Outcome == SignalSaveOutcome.Duplicate, "retry must be idempotent");
    Assert(active.Count == 1, "active scanner filter should return the signal");
    var row = active.Signals.Single();
    Assert(row.FusionEvidenceUid == intake.Lineage.FusionEvidenceUid,
        "scanner must preserve Fusion evidence lineage");
    Assert(row.ThesisUid == intake.Lineage.ThesisUid,
        "scanner must preserve thesis lineage");
    Assert(row.RiskDecisionStatus == SignalScannerContractV1.RiskNotEvaluated,
        "missing risk decision must not become approval");
    Assert(row.IsActive, "candidate must be active inside validity window");
    Assert(expired.Count == 0, "active-only scanner must exclude elapsed signals");
}

static async Task TestLineageMismatchAsync()
{
    var projector = new DeterministicFusionSignalProjector(new FusionSignalProjectorOptions());
    var intake = projector.Project(BuildRequest()).Intake
        ?? throw new InvalidOperationException("projection failed");
    var store = new InMemorySignalStore();
    await store.SaveFusionAsync(intake);
    var changed = intake with
    {
        Lineage = intake.Lineage with { ThesisUid = Guid.Parse("00000000-0000-0000-0000-000000000999") },
    };

    try
    {
        await store.SaveFusionAsync(changed);
        throw new InvalidOperationException("changed duplicate lineage was accepted");
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains("different Fusion lineage", StringComparison.Ordinal))
    {
    }
}

static FusionSignalProjectionRequestV1 BuildRequest()
{
    var asOf = new DateTimeOffset(2026, 7, 1, 9, 30, 0, TimeSpan.Zero);
    var correlationId = "00000000-0000-0000-0000-000000000010";
    var evidenceUid = Guid.Parse("00000000-0000-0000-0000-000000000011");
    var thesisUid = Guid.Parse("00000000-0000-0000-0000-000000000012");
    var requestUid = Guid.Parse("00000000-0000-0000-0000-000000000013");
    var signalUid = Guid.Parse("00000000-0000-0000-0000-000000000014");

    var fusion = new FusionReadyEvidenceV1(
        evidenceUid,
        Guid.Parse("00000000-0000-0000-0000-000000000015"),
        Guid.Parse("00000000-0000-0000-0000-000000000016"),
        Guid.Parse("00000000-0000-0000-0000-000000000017"),
        correlationId,
        "NSE_INDEX|Nifty 50",
        "5m",
        asOf,
        asOf.AddSeconds(1),
        "fusion-weights-v1.0.0",
        new[]
        {
            new FusionDirectionalEvidenceV1(
                Guid.Parse("00000000-0000-0000-0000-000000000018"),
                "TREND",
                "1.0.0",
                "5m",
                "LONG",
                80m,
                75m,
                asOf,
                new[] { "Trend supports long" }),
        },
        new FusionRegimeEvidenceV1(
            Guid.Parse("00000000-0000-0000-0000-000000000019"),
            "TRENDING_NORMAL",
            "1.0.0",
            "5m",
            "LONG",
            75m,
            asOf,
            new[] { "Regime supports long" }),
        new[]
        {
            new FusionTimeframeConfirmationV1(
                "5m",
                Guid.Parse("00000000-0000-0000-0000-000000000020"),
                Guid.Parse("00000000-0000-0000-0000-000000000021"),
                "LONG",
                80m,
                75m,
                true,
                asOf,
                new[] { "Primary confirmation" }),
            new FusionTimeframeConfirmationV1(
                "15m",
                Guid.Parse("00000000-0000-0000-0000-000000000022"),
                Guid.Parse("00000000-0000-0000-0000-000000000023"),
                "LONG",
                72m,
                70m,
                true,
                asOf,
                new[] { "Higher timeframe confirmation" }),
        },
        new FusionTradeProposalV1(
            "LONG",
            25000m,
            24990m,
            25010m,
            24850m,
            new[] { new FusionTradeTargetProposalV1(1, 25300m, 1m) },
            0.001m,
            "atr-trade-proposal-v1.0.0"),
        true,
        Array.Empty<string>());

    var candidate = new CanonicalCandidateSignalV1(
        signalUid,
        ThesisFusionContractV1.CandidateStatus,
        fusion.InstrumentKey,
        EvidenceDirectionV1.Long,
        "5m",
        80m,
        75m,
        asOf.AddSeconds(2),
        "fusion-weights-v1.0.0",
        thesisUid);
    var thesis = new ThesisFusionResultV1(
        thesisUid,
        requestUid,
        correlationId,
        fusion.InstrumentKey,
        ThesisFusionContractV1.CandidateStatus,
        EvidenceDirectionV1.Long,
        80m,
        10m,
        75m,
        "Long thesis accepted",
        Array.Empty<string>(),
        new[]
        {
            new ThesisEvidenceV1(
                "TREND",
                "5m",
                EvidenceDirectionV1.Long,
                80m,
                75m,
                0.20m,
                0.12m,
                new[] { "Trend supports long" }),
        },
        candidate,
        "deterministic-fusion-v1.0.0",
        "fusion-weights-v1.0.0",
        asOf.AddSeconds(2));

    return new FusionSignalProjectionRequestV1(
        fusion,
        thesis,
        thesis.GeneratedAtUtc,
        thesis.GeneratedAtUtc.AddMinutes(2),
        thesis.GeneratedAtUtc.AddMinutes(5),
        30);
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
