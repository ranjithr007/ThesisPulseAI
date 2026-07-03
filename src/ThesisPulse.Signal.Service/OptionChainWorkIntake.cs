using System.Security.Cryptography;
using System.Text;
using ThesisPulse.Shared.Contracts.Intelligence.V1;

namespace ThesisPulse.Signal.Service;

public sealed record OptionChainWorkCandidate(
    Guid SnapshotUid,
    string InstrumentKey,
    DateTimeOffset EventAtUtc,
    DateTimeOffset ReceivedAtUtc,
    int Revision,
    string SnapshotStatus,
    string QualityStatus,
    bool IsPointInTimeEligible);

public interface IOptionChainWorkCandidateSource
{
    Task<IReadOnlyCollection<OptionChainWorkCandidate>> DiscoverAsync(
        DateTimeOffset workflowCutoffUtc,
        DateTimeOffset minimumEventAtUtc,
        int maximumCount,
        string engineVersion,
        string policyVersion,
        CancellationToken cancellationToken = default);
}

public sealed record OptionChainWorkIntakeOptions
{
    public bool Enabled { get; init; }

    public int PollIntervalSeconds { get; init; } = 30;

    public int BatchSize { get; init; } = 25;

    public int MaximumSnapshotAgeSeconds { get; init; } = 420;

    public string EngineVersion { get; init; } = OptionChainIntelligenceContractV1.EngineVersion;

    public string PolicyVersion { get; init; } = OptionChainIntelligenceContractV1.PolicyVersion;

    public void Validate()
    {
        if (PollIntervalSeconds is < 1 or > 900)
            throw new InvalidOperationException("Option-chain intake poll interval must be between 1 and 900 seconds.");
        if (BatchSize is < 1 or > 250)
            throw new InvalidOperationException("Option-chain intake batch size must be between 1 and 250.");
        if (MaximumSnapshotAgeSeconds is < 1 or > 86400)
            throw new InvalidOperationException("Option-chain intake maximum snapshot age must be between 1 and 86400 seconds.");
        ArgumentException.ThrowIfNullOrWhiteSpace(EngineVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(PolicyVersion);
    }
}

public sealed record OptionChainWorkIntakeRunResult(
    int Discovered,
    int Enqueued,
    int Duplicates,
    int Rejected,
    DateTimeOffset WorkflowCutoffUtc);

public sealed record OptionChainWorkIntakeMetrics(
    long Runs,
    long Discovered,
    long Enqueued,
    long Duplicates,
    long Rejected,
    DateTimeOffset? LastRunAtUtc,
    DateTimeOffset ObservedAtUtc);

public static class OptionChainWorkIdentity
{
    public static Guid Create(
        Guid snapshotUid,
        string instrumentKey,
        DateTimeOffset workflowCutoffUtc,
        string engineVersion,
        string policyVersion)
    {
        if (snapshotUid == Guid.Empty)
            throw new ArgumentException("Snapshot UID is required.", nameof(snapshotUid));
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);

        var material = string.Join(
            '|',
            snapshotUid.ToString("N"),
            instrumentKey.Trim().ToUpperInvariant(),
            workflowCutoffUtc.UtcDateTime.ToString("O"),
            engineVersion.Trim(),
            policyVersion.Trim());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new Guid(hash.AsSpan(0, 16));
    }
}

public sealed class OptionChainWorkIntakeState
{
    private long _runs;
    private long _discovered;
    private long _enqueued;
    private long _duplicates;
    private long _rejected;
    private long _lastRunTicks;

    public void Record(OptionChainWorkIntakeRunResult result)
    {
        Interlocked.Increment(ref _runs);
        Interlocked.Add(ref _discovered, result.Discovered);
        Interlocked.Add(ref _enqueued, result.Enqueued);
        Interlocked.Add(ref _duplicates, result.Duplicates);
        Interlocked.Add(ref _rejected, result.Rejected);
        Interlocked.Exchange(ref _lastRunTicks, result.WorkflowCutoffUtc.UtcTicks);
    }

    public OptionChainWorkIntakeMetrics Snapshot(DateTimeOffset observedAtUtc)
    {
        var ticks = Interlocked.Read(ref _lastRunTicks);
        return new OptionChainWorkIntakeMetrics(
            Interlocked.Read(ref _runs),
            Interlocked.Read(ref _discovered),
            Interlocked.Read(ref _enqueued),
            Interlocked.Read(ref _duplicates),
            Interlocked.Read(ref _rejected),
            ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero),
            observedAtUtc);
    }
}

public sealed class OptionChainWorkIntake(
    IOptionChainWorkCandidateSource candidateSource,
    IOptionChainWorkQueue queue,
    OptionChainWorkIntakeOptions options,
    TimeProvider timeProvider,
    OptionChainWorkIntakeState state)
{
    public async Task<OptionChainWorkIntakeRunResult> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        var cutoff = timeProvider.GetUtcNow();
        if (!options.Enabled)
        {
            var disabled = new OptionChainWorkIntakeRunResult(0, 0, 0, 0, cutoff);
            state.Record(disabled);
            return disabled;
        }

        var minimumEventAt = cutoff.AddSeconds(-options.MaximumSnapshotAgeSeconds);
        var candidates = await candidateSource.DiscoverAsync(
            cutoff,
            minimumEventAt,
            options.BatchSize,
            options.EngineVersion,
            options.PolicyVersion,
            cancellationToken);

        var enqueued = 0;
        var duplicates = 0;
        var rejected = 0;
        foreach (var candidate in candidates
                     .OrderBy(value => value.ReceivedAtUtc)
                     .ThenBy(value => value.EventAtUtc)
                     .ThenBy(value => value.SnapshotUid))
        {
            if (!IsEligible(candidate, cutoff, minimumEventAt))
            {
                rejected++;
                continue;
            }

            var workCutoff = candidate.ReceivedAtUtc;
            var now = timeProvider.GetUtcNow();
            var workItem = new OptionChainWorkItem(
                OptionChainWorkIdentity.Create(
                    candidate.SnapshotUid,
                    candidate.InstrumentKey,
                    workCutoff,
                    options.EngineVersion,
                    options.PolicyVersion),
                candidate.SnapshotUid,
                candidate.InstrumentKey,
                workCutoff,
                options.EngineVersion,
                options.PolicyVersion,
                OptionChainWorkStatus.Pending,
                0,
                now,
                null,
                null,
                null,
                now,
                now);

            if (await queue.EnqueueAsync(workItem, cancellationToken))
                enqueued++;
            else
                duplicates++;
        }

        var result = new OptionChainWorkIntakeRunResult(
            candidates.Count,
            enqueued,
            duplicates,
            rejected,
            cutoff);
        state.Record(result);
        return result;
    }

    private static bool IsEligible(
        OptionChainWorkCandidate candidate,
        DateTimeOffset cutoff,
        DateTimeOffset minimumEventAt)
    {
        if (candidate.SnapshotUid == Guid.Empty ||
            string.IsNullOrWhiteSpace(candidate.InstrumentKey) ||
            candidate.Revision < 0)
            return false;
        if (!string.Equals(candidate.SnapshotStatus, "COMPLETE", StringComparison.Ordinal))
            return false;
        if (!string.Equals(candidate.QualityStatus, "VALID", StringComparison.Ordinal))
            return false;
        if (!candidate.IsPointInTimeEligible)
            return false;
        if (candidate.EventAtUtc > cutoff || candidate.ReceivedAtUtc > cutoff)
            return false;
        if (candidate.ReceivedAtUtc < candidate.EventAtUtc)
            return false;
        return candidate.EventAtUtc >= minimumEventAt;
    }
}

public sealed class OptionChainWorkIntakeWorker(
    OptionChainWorkIntakeOptions options,
    OptionChainWorkIntake intake,
    ILogger<OptionChainWorkIntakeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        if (!options.Enabled)
            return;

        var delay = TimeSpan.FromSeconds(options.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await intake.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Option-chain snapshot discovery failed closed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}

internal sealed class EmptyOptionChainWorkCandidateSource : IOptionChainWorkCandidateSource
{
    public Task<IReadOnlyCollection<OptionChainWorkCandidate>> DiscoverAsync(
        DateTimeOffset workflowCutoffUtc,
        DateTimeOffset minimumEventAtUtc,
        int maximumCount,
        string engineVersion,
        string policyVersion,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<OptionChainWorkCandidate>>(
            Array.Empty<OptionChainWorkCandidate>());
}
