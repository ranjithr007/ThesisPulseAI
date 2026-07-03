using ThesisPulse.Shared.Contracts.Intelligence.V1;
using ThesisPulse.Signal.Service;

var failures = new List<string>();

await RunAsync("deterministic work identity", TestDeterministicIdentityAsync, failures);
await RunAsync("eligible snapshot enqueue and duplicate replay", TestEligibleEnqueueAsync, failures);
await RunAsync("invalid snapshots fail closed", TestInvalidCandidatesAsync, failures);
await RunAsync("disabled intake performs no discovery", TestDisabledIntakeAsync, failures);

if (failures.Count > 0)
{
    Console.Error.WriteLine("Phase 4.8 option-chain intake acceptance failed:");
    foreach (var failure in failures)
        Console.Error.WriteLine($"- {failure}");
    return 1;
}

Console.WriteLine("Phase 4.8 option-chain intake acceptance passed.");
return 0;

static async Task RunAsync(
    string name,
    Func<Task> test,
    ICollection<string> failures)
{
    try
    {
        await test();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
    }
}

static Task TestDeterministicIdentityAsync()
{
    var snapshotUid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    var cutoff = DateTimeOffset.Parse("2026-07-03T10:00:00Z");

    var first = OptionChainWorkIdentity.Create(
        snapshotUid,
        "NSE_INDEX:NIFTY 50",
        cutoff,
        OptionChainIntelligenceContractV1.EngineVersion,
        OptionChainIntelligenceContractV1.PolicyVersion);
    var replay = OptionChainWorkIdentity.Create(
        snapshotUid,
        "nse_index:nifty 50",
        cutoff,
        OptionChainIntelligenceContractV1.EngineVersion,
        OptionChainIntelligenceContractV1.PolicyVersion);
    var changedCutoff = OptionChainWorkIdentity.Create(
        snapshotUid,
        "NSE_INDEX:NIFTY 50",
        cutoff.AddTicks(1),
        OptionChainIntelligenceContractV1.EngineVersion,
        OptionChainIntelligenceContractV1.PolicyVersion);

    Assert(first == replay, "Equivalent normalized inputs must produce the same work UID.");
    Assert(first != changedCutoff, "A changed point-in-time cutoff must produce a different work UID.");
    return Task.CompletedTask;
}

static async Task TestEligibleEnqueueAsync()
{
    var now = DateTimeOffset.Parse("2026-07-03T10:00:00Z");
    var candidate = Candidate(
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        now.AddMinutes(-1),
        now.AddSeconds(-30));
    var source = new StaticCandidateSource([candidate]);
    var queue = new InMemoryOptionChainWorkQueue();
    var state = new OptionChainWorkIntakeState();
    var intake = CreateIntake(source, queue, state, now, enabled: true);

    var first = await intake.RunOnceAsync();
    Assert(first.Discovered == 1, "One snapshot should be discovered.");
    Assert(first.Enqueued == 1, "The eligible snapshot should be enqueued.");
    Assert(first.Duplicates == 0, "The first enqueue must not be a duplicate.");
    Assert(first.Rejected == 0, "The eligible snapshot must not be rejected.");

    var queueAfterFirst = await queue.GetMetricsAsync(now);
    Assert(queueAfterFirst.Pending == 1, "The durable queue reference must contain one pending item.");

    var replay = await intake.RunOnceAsync();
    Assert(replay.Enqueued == 0, "Replay must not create a second work item.");
    Assert(replay.Duplicates == 1, "Replay must be reported as a duplicate.");

    var queueAfterReplay = await queue.GetMetricsAsync(now);
    Assert(queueAfterReplay.Pending == 1, "Replay must have exactly-once effect in the queue.");

    var metrics = state.Snapshot(now);
    Assert(metrics.Runs == 2, "Two intake runs should be recorded.");
    Assert(metrics.Enqueued == 1, "Only one enqueue should be recorded.");
    Assert(metrics.Duplicates == 1, "One duplicate replay should be recorded.");
}

static async Task TestInvalidCandidatesAsync()
{
    var now = DateTimeOffset.Parse("2026-07-03T10:00:00Z");
    var candidates = new[]
    {
        Candidate(Guid.Parse("30000000-0000-0000-0000-000000000001"), now.AddMinutes(-1), now.AddSeconds(-30)) with
        {
            SnapshotStatus = "PARTIAL",
        },
        Candidate(Guid.Parse("30000000-0000-0000-0000-000000000002"), now.AddMinutes(-1), now.AddSeconds(-30)) with
        {
            QualityStatus = "INVALID",
        },
        Candidate(Guid.Parse("30000000-0000-0000-0000-000000000003"), now.AddMinutes(-1), now.AddSeconds(-30)) with
        {
            IsPointInTimeEligible = false,
        },
        Candidate(Guid.Parse("30000000-0000-0000-0000-000000000004"), now.AddSeconds(1), now.AddSeconds(2)),
        Candidate(Guid.Parse("30000000-0000-0000-0000-000000000005"), now.AddSeconds(-421), now.AddSeconds(-420)),
        Candidate(Guid.Parse("30000000-0000-0000-0000-000000000006"), now.AddSeconds(-10), now.AddSeconds(-20)),
    };
    var source = new StaticCandidateSource(candidates);
    var queue = new InMemoryOptionChainWorkQueue();
    var state = new OptionChainWorkIntakeState();
    var intake = CreateIntake(source, queue, state, now, enabled: true);

    var result = await intake.RunOnceAsync();
    Assert(result.Discovered == candidates.Length, "All supplied candidates should be inspected.");
    Assert(result.Enqueued == 0, "Invalid candidates must never be enqueued.");
    Assert(result.Rejected == candidates.Length, "Every invalid candidate must fail closed.");

    var queueMetrics = await queue.GetMetricsAsync(now);
    Assert(queueMetrics.Pending == 0, "Rejected candidates must not reach the queue.");
}

static async Task TestDisabledIntakeAsync()
{
    var now = DateTimeOffset.Parse("2026-07-03T10:00:00Z");
    var source = new CountingCandidateSource();
    var queue = new InMemoryOptionChainWorkQueue();
    var state = new OptionChainWorkIntakeState();
    var intake = CreateIntake(source, queue, state, now, enabled: false);

    var result = await intake.RunOnceAsync();
    Assert(source.CallCount == 0, "Disabled intake must not query the candidate source.");
    Assert(result.Discovered == 0 && result.Enqueued == 0, "Disabled intake must remain inert.");
}

static OptionChainWorkIntake CreateIntake(
    IOptionChainWorkCandidateSource source,
    IOptionChainWorkQueue queue,
    OptionChainWorkIntakeState state,
    DateTimeOffset now,
    bool enabled) =>
    new(
        source,
        queue,
        new OptionChainWorkIntakeOptions
        {
            Enabled = enabled,
            PollIntervalSeconds = 30,
            BatchSize = 25,
            MaximumSnapshotAgeSeconds = 420,
        },
        new FixedTimeProvider(now),
        state);

static OptionChainWorkCandidate Candidate(
    Guid snapshotUid,
    DateTimeOffset eventAtUtc,
    DateTimeOffset receivedAtUtc) =>
    new(
        snapshotUid,
        "NSE_INDEX:NIFTY 50",
        eventAtUtc,
        receivedAtUtc,
        0,
        "COMPLETE",
        "VALID",
        true);

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

internal sealed class StaticCandidateSource(
    IReadOnlyCollection<OptionChainWorkCandidate> candidates)
    : IOptionChainWorkCandidateSource
{
    public Task<IReadOnlyCollection<OptionChainWorkCandidate>> DiscoverAsync(
        DateTimeOffset workflowCutoffUtc,
        DateTimeOffset minimumEventAtUtc,
        int maximumCount,
        string engineVersion,
        string policyVersion,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<OptionChainWorkCandidate>>(
            candidates.Take(maximumCount).ToArray());
}

internal sealed class CountingCandidateSource : IOptionChainWorkCandidateSource
{
    public int CallCount { get; private set; }

    public Task<IReadOnlyCollection<OptionChainWorkCandidate>> DiscoverAsync(
        DateTimeOffset workflowCutoffUtc,
        DateTimeOffset minimumEventAtUtc,
        int maximumCount,
        string engineVersion,
        string policyVersion,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult<IReadOnlyCollection<OptionChainWorkCandidate>>(
            Array.Empty<OptionChainWorkCandidate>());
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
